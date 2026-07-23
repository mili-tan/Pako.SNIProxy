using System.Collections.Concurrent;
using System.Net;

namespace Pako.SNIProxy.Dns;

public sealed class DnsCache : IDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly int _maxEntries;
    private readonly Timer _sweepTimer;
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    private readonly record struct CacheEntry(IReadOnlyList<IPAddress> Addresses, DateTime ExpiresAtUtc, bool IsNegative);

    public DnsCache(int maxEntries = 10000)
    {
        _maxEntries = Math.Max(100, maxEntries);
        _sweepTimer = new Timer(Sweep, null, SweepInterval, SweepInterval);
    }

    public bool TryGet(string configKey, string host, out IReadOnlyList<IPAddress> addresses)
    {
        addresses = Array.Empty<IPAddress>();
        string key = MakeKey(configKey, host);

        if (!_entries.TryGetValue(key, out var entry))
            return false;

        if (DateTime.UtcNow >= entry.ExpiresAtUtc)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        if (entry.IsNegative)
            return true;

        addresses = entry.Addresses;
        return true;
    }

    public void Set(string configKey, string host, IReadOnlyList<IPAddress> addresses, int ttlSeconds, int maxTtlSeconds)
    {
        string key = MakeKey(configKey, host);
        int effectiveTtl = Math.Clamp(ttlSeconds, 1, Math.Max(1, maxTtlSeconds));
        var entry = new CacheEntry(addresses, DateTime.UtcNow.AddSeconds(effectiveTtl), IsNegative: addresses.Count == 0);
        _entries[key] = entry;

        if (_entries.Count > _maxEntries)
            Evict();
    }

    public void SetNegative(string configKey, string host)
    {
        string key = MakeKey(configKey, host);
        var entry = new CacheEntry(Array.Empty<IPAddress>(), DateTime.UtcNow.Add(NegativeTtl), IsNegative: true);
        _entries[key] = entry;

        if (_entries.Count > _maxEntries)
            Evict();
    }

    private void Evict()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _entries)
        {
            if (now >= kvp.Value.ExpiresAtUtc)
                _entries.TryRemove(kvp.Key, out _);
        }

        if (_entries.Count <= _maxEntries)
            return;

        // Still over capacity: drop the soonest-expiring entries (plus a 10% margin to avoid thrashing).
        int removeCount = _entries.Count - _maxEntries + (_maxEntries / 10);
        var victims = _entries
            .OrderBy(e => e.Value.ExpiresAtUtc)
            .Take(removeCount)
            .Select(e => e.Key)
            .ToList();

        foreach (var key in victims)
            _entries.TryRemove(key, out _);
    }

    public void Clear() => _entries.Clear();

    public int Count => _entries.Count;

    private void Sweep(object? state)
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _entries)
        {
            if (now >= kvp.Value.ExpiresAtUtc)
                _entries.TryRemove(kvp.Key, out _);
        }
    }

    public void Dispose()
    {
        _sweepTimer.Dispose();
    }

    private static string MakeKey(string configKey, string host) => configKey + "|" + host;
}
