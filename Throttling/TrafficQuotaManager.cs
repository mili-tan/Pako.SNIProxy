using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Pako.SNIProxy.Auth;
using Pako.SNIProxy.Configuration;

namespace Pako.SNIProxy.Throttling;

public sealed class TrafficQuotaManager : IDisposable
{
    private sealed class Counters
    {
        public long DayBytes;
        public long MonthBytes;
        public string DayKey = string.Empty;
        public string MonthKey = string.Empty;
        public long PendingFlush;
        public DateTime LastUsedUtc = DateTime.UtcNow;
        public readonly object Sync = new();
    }

    private readonly ConcurrentDictionary<IPAddress, Counters> _counters = new();
    private readonly Persistence.TrafficStore _store;
    private readonly TrafficQuotaOptions _options;
    private readonly ClientPolicyResolver _policyResolver;
    private readonly ILogger<TrafficQuotaManager> _logger;
    private readonly Timer? _flushTimer;
    private DateTime _lastMaintenanceUtc = DateTime.UtcNow;

    public bool Enabled => _options.Enabled;

    public TrafficQuotaManager(
        Persistence.TrafficStore store,
        TrafficQuotaOptions options,
        ClientPolicyResolver policyResolver,
        ILogger<TrafficQuotaManager> logger)
    {
        _store = store;
        _options = options;
        _policyResolver = policyResolver;
        _logger = logger;

        if (_options.Enabled)
        {
            _flushTimer = new Timer(Flush, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    public bool IsWithinQuota(IPAddress clientIp, out string? reason)
    {
        reason = null;
        var policy = _policyResolver.Resolve(clientIp);
        if (!policy.QuotaEnabled)
            return true;

        var counters = GetOrLoad(clientIp);
        long day, month;
        lock (counters.Sync)
        {
            RollOverIfNeeded(counters);
            counters.LastUsedUtc = DateTime.UtcNow;
            day = counters.DayBytes;
            month = counters.MonthBytes;
        }

        if (day >= policy.DailyLimitBytes)
        {
            reason = $"Daily traffic quota ({FormatBytes(policy.DailyLimitBytes)}) exceeded for {clientIp}.";
            return false;
        }
        if (month >= policy.MonthlyLimitBytes)
        {
            reason = $"Monthly traffic quota ({FormatBytes(policy.MonthlyLimitBytes)}) exceeded for {clientIp}.";
            return false;
        }
        return true;
    }

    public void RecordBytes(IPAddress clientIp, long bytes)
    {
        if (!_options.Enabled || bytes <= 0)
            return;

        var counters = GetOrLoad(clientIp);
        lock (counters.Sync)
        {
            RollOverIfNeeded(counters);
            counters.DayBytes += bytes;
            counters.MonthBytes += bytes;
            counters.PendingFlush += bytes;
            counters.LastUsedUtc = DateTime.UtcNow;
        }
    }

    public (long DayBytes, long MonthBytes) GetUsage(IPAddress clientIp)
    {
        var counters = GetOrLoad(clientIp);
        lock (counters.Sync)
        {
            RollOverIfNeeded(counters);
            counters.LastUsedUtc = DateTime.UtcNow;
            return (counters.DayBytes, counters.MonthBytes);
        }
    }

    public void Reset(IPAddress clientIp)
    {
        if (_counters.TryGetValue(clientIp, out var counters))
        {
            lock (counters.Sync)
            {
                counters.DayBytes = 0;
                counters.MonthBytes = 0;
                counters.PendingFlush = 0;
            }
        }
    }

    private Counters GetOrLoad(IPAddress clientIp)
    {
        return _counters.GetOrAdd(clientIp, ip =>
        {
            var c = new Counters();
            var now = DateTime.UtcNow;
            c.DayKey = now.ToString("yyyy-MM-dd");
            c.MonthKey = now.ToString("yyyy-MM");

            if (_options.Enabled)
            {
                c.DayBytes = _store.GetBytes(ip.ToString(), "day", c.DayKey);
                c.MonthBytes = _store.GetBytes(ip.ToString(), "month", c.MonthKey);
            }
            return c;
        });
    }

    private void RollOverIfNeeded(Counters counters)
    {
        var now = DateTime.UtcNow;
        string dayKey = now.ToString("yyyy-MM-dd");
        string monthKey = now.ToString("yyyy-MM");

        if (counters.DayKey == dayKey && counters.MonthKey == monthKey)
            return;

        if (counters.DayKey != dayKey)
            counters.DayBytes = 0;
        if (counters.MonthKey != monthKey)
            counters.MonthBytes = 0;

        counters.DayKey = dayKey;
        counters.MonthKey = monthKey;
    }

    private void Flush(object? state)
    {
        try
        {
            var idleThreshold = DateTime.UtcNow.AddHours(-1);

            foreach (var kvp in _counters)
            {
                var counters = kvp.Value;
                long toFlush;
                string dayKey, monthKey;
                bool idle;
                lock (counters.Sync)
                {
                    toFlush = counters.PendingFlush;
                    dayKey = counters.DayKey;
                    monthKey = counters.MonthKey;
                    idle = counters.LastUsedUtc < idleThreshold;

                    if (toFlush > 0)
                    {
                        counters.PendingFlush = 0;
                    }
                }

                if (toFlush > 0)
                    _store.AddBytes(kvp.Key.ToString(), dayKey, monthKey, toFlush);

                // Evict idle counters once their pending bytes are flushed to bound memory.
                if (idle && toFlush <= 0)
                    _counters.TryRemove(new KeyValuePair<IPAddress, Counters>(kvp.Key, counters));
            }

            // Periodic DB maintenance: WAL checkpoint + old record cleanup.
            if (DateTime.UtcNow - _lastMaintenanceUtc > TimeSpan.FromMinutes(10))
            {
                _lastMaintenanceUtc = DateTime.UtcNow;
                _store.Checkpoint();
                _store.CleanupOldRecords(keepDays: 60);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Traffic quota flush error.");
        }
    }

    public void FlushNow() => Flush(null);

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        if (_options.Enabled)
            FlushNow();
    }
}
