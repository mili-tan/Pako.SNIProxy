using System.Net;
using Pako.SNIProxy.Configuration;
using Pako.SNIProxy.Infrastructure;

namespace Pako.SNIProxy.Auth;

public sealed class IpCidrAuthenticator : IClientAuthenticator
{
    private readonly record struct CidrEntry(string Original, IPNetwork Network);

    private sealed class Snapshot
    {
        public required bool AllowAll { get; init; }
        public required CidrEntry[] Entries { get; init; }
    }

    private readonly object _sync = new();
    private volatile Snapshot _snapshot;

    public IpCidrAuthenticator(ClientAuthOptions options)
    {
        _snapshot = BuildSnapshot(options.AllowAll, options.Whitelist);
    }

    public bool AllowAll => _snapshot.AllowAll;

    public bool IsAllowed(IPAddress clientIp)
    {
        var snap = _snapshot;
        if (snap.AllowAll)
            return true;

        var normalized = IpUtils.Normalize(clientIp);
        foreach (var entry in snap.Entries)
        {
            if (entry.Network.Contains(normalized))
                return true;
        }
        return false;
    }

    public IReadOnlyList<string> GetWhitelist()
    {
        return _snapshot.Entries.Select(e => e.Original).ToList();
    }

    public void AddEntry(string entry)
    {
        lock (_sync)
        {
            var snap = _snapshot;
            if (snap.Entries.Any(e => string.Equals(e.Original, entry, StringComparison.OrdinalIgnoreCase)))
                return;

            if (!IpUtils.TryParseNetwork(entry, out var network))
                throw new ArgumentException($"Invalid IP/CIDR entry: {entry}");

            var entries = snap.Entries.Append(new CidrEntry(entry, network)).ToArray();
            _snapshot = new Snapshot { AllowAll = snap.AllowAll, Entries = entries };
        }
    }

    public bool RemoveEntry(string entry)
    {
        lock (_sync)
        {
            var snap = _snapshot;
            var entries = snap.Entries
                .Where(e => !string.Equals(e.Original, entry, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (entries.Length == snap.Entries.Length)
                return false;
            _snapshot = new Snapshot { AllowAll = snap.AllowAll, Entries = entries };
            return true;
        }
    }

    public void SetAllowAll(bool allowAll)
    {
        lock (_sync)
        {
            var snap = _snapshot;
            _snapshot = new Snapshot { AllowAll = allowAll, Entries = snap.Entries };
        }
    }

    private static Snapshot BuildSnapshot(bool allowAll, IEnumerable<string> whitelist)
    {
        var entries = new List<CidrEntry>();
        foreach (var item in whitelist)
        {
            if (IpUtils.TryParseNetwork(item, out var network))
                entries.Add(new CidrEntry(item, network));
        }
        return new Snapshot { AllowAll = allowAll, Entries = entries.ToArray() };
    }
}
