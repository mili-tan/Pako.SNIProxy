using System.Diagnostics;

namespace Pako.SNIProxy.Throttling;

public sealed class TokenBucket
{
    private readonly double _ratePerSecond;
    private readonly double _capacity;
    private readonly object _lock = new();
    private double _tokens;
    private long _lastRefillTimestamp;

    public long RateBytesPerSecond { get; }
    public long BurstBytes { get; }
    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    public TokenBucket(long bytesPerSecond, long burstBytes)
    {
        RateBytesPerSecond = bytesPerSecond;
        BurstBytes = burstBytes;
        _ratePerSecond = bytesPerSecond;
        _capacity = burstBytes;
        _tokens = burstBytes;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
    }

    public TimeSpan Consume(long amount)
    {
        lock (_lock)
        {
            Refill();
            LastUsedUtc = DateTime.UtcNow;

            if (_tokens >= amount)
            {
                _tokens -= amount;
                return TimeSpan.Zero;
            }

            double deficit = amount - _tokens;
            _tokens = 0;

            if (_ratePerSecond <= 0)
                return TimeSpan.Zero;

            return TimeSpan.FromSeconds(deficit / _ratePerSecond);
        }
    }

    private void Refill()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSeconds = (now - _lastRefillTimestamp) / (double)Stopwatch.Frequency;
        _lastRefillTimestamp = now;

        if (elapsedSeconds <= 0)
            return;

        _tokens = Math.Min(_capacity, _tokens + elapsedSeconds * _ratePerSecond);
    }
}
