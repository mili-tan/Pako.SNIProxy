using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pako.SNIProxy.Auth;
using Pako.SNIProxy.Configuration;
using Pako.SNIProxy.Core;
using Pako.SNIProxy.Dns;
using Pako.SNIProxy.Infrastructure;
using Pako.SNIProxy.Routing;
using Pako.SNIProxy.Throttling;

namespace Pako.SNIProxy.Core;

public sealed class SniProxyService : BackgroundService
{
    private const int MaxInitialRead = 16384;
    private const int DefaultTargetPort = 443;

    private readonly ProxyOptions _options;
    private readonly IClientAuthenticator _authenticator;
    private readonly ConnectionLimiter _connectionLimiter;
    private readonly TrafficQuotaManager _quota;
    private readonly SiteRouter _router;
    private readonly DnsResolverManager _dns;
    private readonly ConnectionRelay _relay;
    private readonly ConnectionRegistry _registry;
    private readonly ILogger<SniProxyService> _logger;

    private Socket? _listenSocket;

    public SniProxyService(
        ProxyOptions options,
        IClientAuthenticator authenticator,
        ConnectionLimiter connectionLimiter,
        TrafficQuotaManager quota,
        SiteRouter router,
        DnsResolverManager dns,
        ConnectionRelay relay,
        ConnectionRegistry registry,
        ILogger<SniProxyService> logger)
    {
        _options = options;
        _authenticator = authenticator;
        _connectionLimiter = connectionLimiter;
        _quota = quota;
        _router = router;
        _dns = dns;
        _relay = relay;
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listenSocket = CreateListenSocket();
        var localEp = _listenSocket.LocalEndPoint as IPEndPoint;
        _logger.LogInformation("SNI Proxy listening on {Endpoint} (mode={Mode})", localEp, _options.RouteMode);

        while (!stoppingToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listenSocket.AcceptAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Accept error.");
                continue;
            }

            _ = ProcessClientAsync(client, stoppingToken);
        }
    }

    private Socket CreateListenSocket()
    {
        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        var address = IPAddress.TryParse(_options.ListenAddress, out var parsed)
            ? (parsed.AddressFamily == AddressFamily.InterNetwork ? parsed.MapToIPv6() : parsed)
            : IPAddress.IPv6Any;

        socket.Bind(new IPEndPoint(address, _options.ListenPort));
        socket.Listen(_options.Connection.Backlog);
        return socket;
    }

    private async Task ProcessClientAsync(Socket client, CancellationToken stoppingToken)
    {
        var clientIp = IpUtils.GetClientIp(client);
        if (clientIp is null)
        {
            SafeClose(client);
            return;
        }

        if (!_authenticator.IsAllowed(clientIp))
        {
            _logger.LogWarning("Rejected client {Ip}: not in whitelist.", clientIp);
            SafeClose(client);
            return;
        }

        if (!_connectionLimiter.TryAcquire(clientIp, out var limitReason))
        {
            _logger.LogWarning("Rejected client {Ip}: {Reason}", clientIp, limitReason);
            SafeClose(client);
            return;
        }

        try
        {
            if (!_quota.IsWithinQuota(clientIp, out var quotaReason))
            {
                _logger.LogWarning("Rejected client {Ip}: {Reason}", clientIp, quotaReason);
                SafeClose(client);
                return;
            }

            var context = new ConnectionContext { ClientIp = clientIp, ClientSocket = client };
            _registry.Add(context);
            try
            {
                await HandleConnectionAsync(context, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                _registry.Remove(context.Id);
                context.Dispose();
            }
        }
        finally
        {
            _connectionLimiter.Release(clientIp);
        }
    }

    private async Task HandleConnectionAsync(ConnectionContext context, CancellationToken stoppingToken)
    {
        var client = context.ClientSocket!;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxInitialRead);
        try
        {
            int received = await ReadInitialAsync(client, buffer, stoppingToken).ConfigureAwait(false);
            if (received <= 0)
                return;

            if (!SniParser.TryExtractSni(buffer.AsSpan(0, received), out var sni) || sni is null)
            {
                _logger.LogDebug("No SNI extracted from {Ip}.", context.ClientIp);
                return;
            }

            context.Sni = sni;

            var decision = _router.Route(sni);
            if (!decision.Allowed)
            {
                _logger.LogInformation("Denied SNI {Sni} from {Ip}: {Reason}", sni, context.ClientIp, decision.DenyReason);
                return;
            }

            IPEndPoint? target = await ResolveTargetAsync(sni, decision, stoppingToken).ConfigureAwait(false);
            if (target is null)
            {
                _logger.LogWarning("Failed to resolve target for SNI {Sni}.", sni);
                return;
            }

            context.TargetEndpoint = target;

            var backend = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            context.BackendSocket = backend;

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            connectCts.CancelAfter(_options.Connection.ConnectTimeoutMs);
            try
            {
                await backend.ConnectAsync(target, connectCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Backend connect to {Target} failed: {Msg}", target, ex.Message);
                return;
            }

            await backend.SendAsync(new Memory<byte>(buffer, 0, received), SocketFlags.None, stoppingToken).ConfigureAwait(false);

            _logger.LogDebug("Routing {Sni} ({Ip}) -> {Target}", sni, context.ClientIp, target);

            await _relay.RelayAsync(context, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Connection handling error for {Ip}.", context.ClientIp);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<int> ReadInitialAsync(Socket client, byte[] buffer, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(_options.Connection.InitialReadTimeoutMs);

        int received = 0;
        while (received < MaxInitialRead)
        {
            int read;
            try
            {
                read = await client
                    .ReceiveAsync(new Memory<byte>(buffer, received, MaxInitialRead - received), SocketFlags.None, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return received;
            }
            catch (SocketException)
            {
                return received;
            }

            if (read == 0)
                return received;

            received += read;

            if (SniParser.TryExtractSni(buffer.AsSpan(0, received), out _))
                return received;
        }

        return received;
    }

    private async Task<IPEndPoint?> ResolveTargetAsync(string sni, RouteDecision decision, CancellationToken cancellationToken)
    {
        if (decision.PinnedEndpoint is not null)
            return decision.PinnedEndpoint;

        if (decision.DnsOptions is null)
            return null;

        var addresses = await _dns.ResolveAsync(sni, decision.DnsOptions, cancellationToken).ConfigureAwait(false);
        if (addresses.Count == 0)
            return null;

        // Prefer IPv4 for broader compatibility; fall back to first result.
        var preferred = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        return new IPEndPoint(preferred, DefaultTargetPort);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SNI Proxy stopping; draining {Count} connections.", _registry.Count);
        try { _listenSocket?.Close(); } catch { }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void SafeClose(Socket socket)
    {
        try { socket.Close(); } catch { }
    }
}
