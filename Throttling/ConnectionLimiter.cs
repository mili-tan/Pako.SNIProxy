using System.Collections.Concurrent;
using System.Net;
using Pako.SNIProxy.Configuration;

namespace Pako.SNIProxy.Throttling;

public sealed class ConnectionLimiter
{
    private readonly ConcurrentDictionary<IPAddress, int> _perIp = new();
    private readonly ConnectionLimitOptions _options;
    private int _total;

    public bool Enabled => _options.Enabled;
    public int TotalConnections => Volatile.Read(ref _total);

    public ConnectionLimiter(ConnectionLimitOptions options)
    {
        _options = options;
    }

    public bool TryAcquire(IPAddress clientIp, out string? reason)
    {
        reason = null;
        if (!_options.Enabled)
            return true;

        int currentTotal = Interlocked.Increment(ref _total);
        if (currentTotal > _options.MaxTotal)
        {
            Interlocked.Decrement(ref _total);
            reason = $"Total connection limit ({_options.MaxTotal}) reached.";
            return false;
        }

        int perIp = _perIp.AddOrUpdate(clientIp, 1, (_, v) => v + 1);
        if (perIp > _options.MaxPerClientIp)
        {
            ReleasePerIp(clientIp);
            Interlocked.Decrement(ref _total);
            reason = $"Per-IP connection limit ({_options.MaxPerClientIp}) reached for {clientIp}.";
            return false;
        }

        return true;
    }

    public void Release(IPAddress clientIp)
    {
        if (!_options.Enabled)
            return;

        ReleasePerIp(clientIp);
        Interlocked.Decrement(ref _total);
    }

    private void ReleasePerIp(IPAddress clientIp)
    {
        while (true)
        {
            if (!_perIp.TryGetValue(clientIp, out int current))
                return;

            if (current <= 1)
            {
                if (_perIp.TryRemove(new KeyValuePair<IPAddress, int>(clientIp, current)))
                    return;
            }
            else
            {
                if (_perIp.TryUpdate(clientIp, current - 1, current))
                    return;
            }
        }
    }

    public int GetCountForIp(IPAddress clientIp)
        => _perIp.TryGetValue(clientIp, out var count) ? count : 0;
}
