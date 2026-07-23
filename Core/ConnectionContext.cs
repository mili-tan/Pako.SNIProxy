using System.Net;
using System.Net.Sockets;

namespace Pako.SNIProxy.Core;

public sealed class ConnectionContext : IDisposable
{
    private static long _nextId;
    private long _bytesIn;
    private long _bytesOut;
    private int _disposed;

    public long Id { get; } = Interlocked.Increment(ref _nextId);
    public required IPAddress ClientIp { get; init; }
    public string? Sni { get; set; }
    public IPEndPoint? TargetEndpoint { get; set; }
    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;

    public Socket? ClientSocket { get; set; }
    public Socket? BackendSocket { get; set; }

    public void AddBytesIn(long count) => Interlocked.Add(ref _bytesIn, count);
    public void AddBytesOut(long count) => Interlocked.Add(ref _bytesOut, count);
    public long GetBytesIn() => Interlocked.Read(ref _bytesIn);
    public long GetBytesOut() => Interlocked.Read(ref _bytesOut);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        TryClose(ClientSocket);
        TryClose(BackendSocket);
    }

    private static void TryClose(Socket? socket)
    {
        if (socket is null) return;
        try { socket.Shutdown(SocketShutdown.Both); } catch { }
        try { socket.Close(); } catch { }
    }
}
