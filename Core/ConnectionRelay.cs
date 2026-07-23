using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Pako.SNIProxy.Throttling;

namespace Pako.SNIProxy.Core;

public sealed class ConnectionRelay
{
    private readonly RateLimiter _rateLimiter;
    private readonly TrafficQuotaManager _quota;
    private readonly int _bufferSize;
    private readonly int _idleTimeoutMs;

    private sealed class ActivityTracker
    {
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;
        public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        public TimeSpan Idle => DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
    }

    public ConnectionRelay(RateLimiter rateLimiter, TrafficQuotaManager quota, int bufferSize, int idleTimeoutMs)
    {
        _rateLimiter = rateLimiter;
        _quota = quota;
        _bufferSize = bufferSize;
        _idleTimeoutMs = idleTimeoutMs;
    }

    public async Task RelayAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        var client = context.ClientSocket!;
        var backend = context.BackendSocket!;

        client.NoDelay = true;
        backend.NoDelay = true;

        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tracker = new ActivityTracker();
        using var watchdog = StartWatchdog(tracker, relayCts);

        var clientToBackend = CopyAsync(client, backend, context.ClientIp, bytes =>
        {
            context.AddBytesIn(bytes);
            _quota.RecordBytes(context.ClientIp, bytes);
        }, tracker, relayCts.Token);

        var backendToClient = CopyAsync(backend, client, context.ClientIp, bytes =>
        {
            context.AddBytesOut(bytes);
            _quota.RecordBytes(context.ClientIp, bytes);
        }, tracker, relayCts.Token);

        try
        {
            await Task.WhenAny(clientToBackend, backendToClient).ConfigureAwait(false);
        }
        finally
        {
            relayCts.Cancel();
            TryShutdown(client);
            TryShutdown(backend);
            try { await Task.WhenAll(clientToBackend, backendToClient).ConfigureAwait(false); } catch { }
        }
    }

    private async Task CopyAsync(
        Socket from,
        Socket to,
        IPAddress clientIp,
        Action<long> onBytes,
        ActivityTracker tracker,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await from
                        .ReceiveAsync(new Memory<byte>(buffer), SocketFlags.None, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }

                if (bytesRead == 0)
                    break;

                tracker.Touch();

                await _rateLimiter.ThrottleAsync(clientIp, bytesRead, cancellationToken).ConfigureAwait(false);

                onBytes(bytesRead);

                int offset = 0;
                while (offset < bytesRead)
                {
                    int sent;
                    try
                    {
                        sent = await to
                            .SendAsync(new Memory<byte>(buffer, offset, bytesRead - offset), SocketFlags.None, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (SocketException) { return; }
                    catch (ObjectDisposedException) { return; }

                    if (sent == 0)
                        return;
                    offset += sent;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private Timer StartWatchdog(ActivityTracker tracker, CancellationTokenSource relayCts)
    {
        if (_idleTimeoutMs <= 0)
            return new Timer(_ => { });

        return new Timer(_ =>
        {
            try
            {
                if (tracker.Idle.TotalMilliseconds > _idleTimeoutMs)
                    relayCts.Cancel();
            }
            catch
            {
                // ignore
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private static void TryShutdown(Socket socket)
    {
        try { socket.Shutdown(SocketShutdown.Both); } catch { }
    }
}
