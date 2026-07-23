using System.Net;
using Pako.SNIProxy.Configuration;
using Pako.SNIProxy.Infrastructure;

namespace Pako.SNIProxy.Auth;

public readonly record struct ClientPolicy(
    bool RateLimitEnabled,
    long RateBytesPerSecond,
    long RateBurstBytes,
    bool QuotaEnabled,
    long DailyLimitBytes,
    long MonthlyLimitBytes);

public sealed class ClientPolicyResolver
{
    private sealed class CompiledRule
    {
        public required string Pattern { get; init; }
        public required IPNetwork Network { get; init; }
        public RateLimitOptions? RateLimit { get; init; }
        public QuotaLimitOptions? Quota { get; init; }
    }

    private sealed class Snapshot
    {
        public required CompiledRule[] Rules { get; init; }
        public required RateLimitOptions GlobalRate { get; init; }
        public required TrafficQuotaOptions GlobalQuota { get; init; }
    }

    private readonly object _sync = new();
    private volatile Snapshot _snapshot;

    public ClientPolicyResolver(ProxyOptions options)
    {
        _snapshot = BuildSnapshot(options.ClientRules, options.RateLimit, options.TrafficQuota);
    }

    public ClientPolicy Resolve(IPAddress clientIp)
    {
        var snap = _snapshot;
        var normalized = IpUtils.Normalize(clientIp);

        foreach (var rule in snap.Rules)
        {
            if (!rule.Network.Contains(normalized))
                continue;

            var rate = rule.RateLimit ?? snap.GlobalRate;
            var quota = rule.Quota;

            return new ClientPolicy(
                RateLimitEnabled: rate.Enabled,
                RateBytesPerSecond: rate.BytesPerSecond,
                RateBurstBytes: rate.BurstBytes,
                QuotaEnabled: quota?.Enabled ?? snap.GlobalQuota.Enabled,
                DailyLimitBytes: quota?.DailyLimitBytes ?? snap.GlobalQuota.DailyLimitBytes,
                MonthlyLimitBytes: quota?.MonthlyLimitBytes ?? snap.GlobalQuota.MonthlyLimitBytes);
        }

        return new ClientPolicy(
            RateLimitEnabled: snap.GlobalRate.Enabled,
            RateBytesPerSecond: snap.GlobalRate.BytesPerSecond,
            RateBurstBytes: snap.GlobalRate.BurstBytes,
            QuotaEnabled: snap.GlobalQuota.Enabled,
            DailyLimitBytes: snap.GlobalQuota.DailyLimitBytes,
            MonthlyLimitBytes: snap.GlobalQuota.MonthlyLimitBytes);
    }

    public IReadOnlyList<ClientRule> GetRules()
    {
        return _snapshot.Rules.Select(r => new ClientRule
        {
            Pattern = r.Pattern,
            RateLimit = r.RateLimit,
            TrafficQuota = r.Quota
        }).ToList();
    }

    public void AddOrUpdate(ClientRule rule)
    {
        if (!IpUtils.TryParseNetwork(rule.Pattern, out var network))
            throw new ArgumentException($"Invalid IP/CIDR pattern: {rule.Pattern}");

        lock (_sync)
        {
            var snap = _snapshot;
            var rules = snap.Rules
                .Where(r => !string.Equals(r.Pattern, rule.Pattern, StringComparison.OrdinalIgnoreCase))
                .ToList();
            rules.Add(new CompiledRule
            {
                Pattern = rule.Pattern,
                Network = network,
                RateLimit = rule.RateLimit,
                Quota = rule.TrafficQuota
            });
            _snapshot = new Snapshot { Rules = rules.ToArray(), GlobalRate = snap.GlobalRate, GlobalQuota = snap.GlobalQuota };
        }
    }

    public bool Remove(string pattern)
    {
        lock (_sync)
        {
            var snap = _snapshot;
            var rules = snap.Rules
                .Where(r => !string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (rules.Length == snap.Rules.Length)
                return false;
            _snapshot = new Snapshot { Rules = rules, GlobalRate = snap.GlobalRate, GlobalQuota = snap.GlobalQuota };
            return true;
        }
    }

    private static Snapshot BuildSnapshot(IEnumerable<ClientRule> rules, RateLimitOptions globalRate, TrafficQuotaOptions globalQuota)
    {
        var compiled = new List<CompiledRule>();
        foreach (var rule in rules)
        {
            if (!IpUtils.TryParseNetwork(rule.Pattern, out var network))
                continue;
            compiled.Add(new CompiledRule
            {
                Pattern = rule.Pattern,
                Network = network,
                RateLimit = rule.RateLimit,
                Quota = rule.TrafficQuota
            });
        }
        return new Snapshot { Rules = compiled.ToArray(), GlobalRate = globalRate, GlobalQuota = globalQuota };
    }
}
