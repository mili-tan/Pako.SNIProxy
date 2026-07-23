using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Pako.SNIProxy.Configuration;

namespace Pako.SNIProxy.Dns;

public sealed class DnsResolverManager : IDisposable
{
    private readonly ConcurrentDictionary<string, IDnsResolver> _resolvers = new();
    private readonly DnsCache _cache;
    private readonly ILogger<DnsResolverManager> _logger;

    public DnsResolverManager(DnsCache cache, ILogger<DnsResolverManager> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public DnsCache Cache => _cache;

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, DnsOptions options, CancellationToken cancellationToken = default)
    {
        string configKey = GetConfigKey(options);

        if (_cache.TryGet(configKey, host, out var cached))
            return cached;

        var resolver = _resolvers.GetOrAdd(configKey, _ => CreateResolver(options));

        IReadOnlyList<DnsAnswer> answers;
        try
        {
            answers = await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DNS resolution failed for {Host}", host);
            return Array.Empty<IPAddress>();
        }

        if (answers.Count == 0)
        {
            _cache.SetNegative(configKey, host);
            return Array.Empty<IPAddress>();
        }

        var addresses = answers.Select(a => a.Address).ToList();
        int minTtl = answers.Min(a => a.TtlSeconds);
        _cache.Set(configKey, host, addresses, minTtl, options.CacheTtlSeconds);

        return addresses;
    }

    private IDnsResolver CreateResolver(DnsOptions options)
    {
        return options.Mode switch
        {
            DnsMode.System => new SystemDnsResolver(),
            DnsMode.Udp => ArSoftDnsResolver.CreateUdp(options.Servers, options.TimeoutMs),
            DnsMode.DoH => ArSoftDnsResolver.CreateDoH(options.Servers, options.TimeoutMs),
            _ => throw new ArgumentOutOfRangeException(nameof(options.Mode), options.Mode, "Unknown DNS mode.")
        };
    }

    public static string GetConfigKey(DnsOptions options)
    {
        var sb = new StringBuilder();
        sb.Append(options.Mode).Append(':');
        foreach (var s in options.Servers.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(s).Append(',');

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()), hash);
        return Convert.ToHexString(hash)[..16];
    }

    public void Dispose()
    {
        foreach (var resolver in _resolvers.Values)
        {
            if (resolver is IDisposable disposable)
                disposable.Dispose();
        }
        _resolvers.Clear();
    }
}
