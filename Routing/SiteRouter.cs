using System.Net;
using Pako.SNIProxy.Configuration;

namespace Pako.SNIProxy.Routing;

public sealed class RouteDecision
{
    public bool Allowed { get; init; }
    public IPEndPoint? PinnedEndpoint { get; init; }
    public DnsOptions? DnsOptions { get; init; }
    public string? DenyReason { get; init; }

    public static RouteDecision Deny(string reason) => new() { Allowed = false, DenyReason = reason };
}

public sealed class SiteRouter
{
    private sealed class CompiledRule
    {
        public required string Pattern { get; init; }
        public DnsOptions? Dns { get; init; }
        public IPEndPoint? PinnedEndpoint { get; init; }
    }

    private sealed class Snapshot
    {
        public required RouteMode Mode { get; init; }
        public required CompiledRule[] Rules { get; init; }
        public required DnsOptions GlobalDns { get; init; }
    }

    private readonly object _sync = new();
    private volatile Snapshot _snapshot;

    public SiteRouter(ProxyOptions options)
    {
        _snapshot = BuildSnapshot(options.RouteMode, options.SiteRules, options.Dns);
    }

    public RouteMode Mode => _snapshot.Mode;

    public RouteDecision Route(string sni)
    {
        var snap = _snapshot;

        foreach (var rule in snap.Rules)
        {
            if (!WildcardMatcher.IsMatch(rule.Pattern, sni))
                continue;

            if (rule.PinnedEndpoint is not null)
                return new RouteDecision { Allowed = true, PinnedEndpoint = rule.PinnedEndpoint };

            return new RouteDecision { Allowed = true, DnsOptions = rule.Dns ?? snap.GlobalDns };
        }

        if (snap.Mode == RouteMode.AllowAll)
            return new RouteDecision { Allowed = true, DnsOptions = snap.GlobalDns };

        return RouteDecision.Deny($"SNI '{sni}' not in whitelist.");
    }

    public IReadOnlyList<string> GetPatterns()
    {
        return _snapshot.Rules.Select(r => r.Pattern).ToList();
    }

    public void AddOrUpdateRule(SiteRule rule)
    {
        lock (_sync)
        {
            var snap = _snapshot;
            var rules = snap.Rules.Where(r => !string.Equals(r.Pattern, rule.Pattern, StringComparison.OrdinalIgnoreCase)).ToList();
            rules.Add(Compile(rule));
            _snapshot = new Snapshot { Mode = snap.Mode, Rules = rules.ToArray(), GlobalDns = snap.GlobalDns };
        }
    }

    public bool RemoveRule(string pattern)
    {
        lock (_sync)
        {
            var snap = _snapshot;
            var rules = snap.Rules.Where(r => !string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rules.Count == snap.Rules.Length)
                return false;
            _snapshot = new Snapshot { Mode = snap.Mode, Rules = rules.ToArray(), GlobalDns = snap.GlobalDns };
            return true;
        }
    }

    public void SetMode(RouteMode mode)
    {
        lock (_sync)
        {
            var snap = _snapshot;
            _snapshot = new Snapshot { Mode = mode, Rules = snap.Rules, GlobalDns = snap.GlobalDns };
        }
    }

    private static Snapshot BuildSnapshot(RouteMode mode, IEnumerable<SiteRule> rules, DnsOptions globalDns)
    {
        return new Snapshot
        {
            Mode = mode,
            Rules = rules.Select(Compile).ToArray(),
            GlobalDns = globalDns
        };
    }

    private static CompiledRule Compile(SiteRule rule)
    {
        IPEndPoint? pinned = null;
        if (!string.IsNullOrWhiteSpace(rule.PinnedEndpoint) && IPEndPoint.TryParse(rule.PinnedEndpoint, out var ep))
            pinned = ep;

        return new CompiledRule
        {
            Pattern = rule.Pattern,
            Dns = rule.Dns,
            PinnedEndpoint = pinned
        };
    }
}
