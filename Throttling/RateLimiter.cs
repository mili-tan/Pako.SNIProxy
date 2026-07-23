using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Pako.SNIProxy.Auth;

namespace Pako.SNIProxy.Throttling;

public sealed class RateLimiter : IDisposable
{
    private readonly ConcurrentDictionary<IPAddress, TokenBucket> _buckets = new();
    private readonly ClientPolicyResolver _policyResolver;
    private readonly ILogger<RateLimiter> _logger;
    private readonly Timer _cleanupTimer;

    public RateLimiter(ClientPolicyResolver policyResolver, ILogger<RateLimiter> logger)
    {
        _policyResolver = policyResolver;
        _logger = logger;
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public async ValueTask ThrottleAsync(IPAddress clientIp, long bytes, CancellationToken cancellationToken = default)
    {
        if (bytes <= 0)
            return;

        var policy = _policyResolver.Resolve(clientIp);
        if (!policy.RateLimitEnabled || policy.RateBytesPerSecond <= 0)
            return;

        var bucket = _buckets.GetOrAdd(clientIp,
            _ => new TokenBucket(policy.RateBytesPerSecond, policy.RateBurstBytes));

        // Recreate the bucket if the effective policy changed for this client.
        if (bucket.RateBytesPerSecond != policy.RateBytesPerSecond || bucket.BurstBytes != policy.RateBurstBytes)
        {
            bucket = new TokenBucket(policy.RateBytesPerSecond, policy.RateBurstBytes);
            _buckets[clientIp] = bucket;
        }

        TimeSpan delay = bucket.Consume(bytes);
        if (delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore; caller will handle cancellation
            }
        }
    }

    private void Cleanup(object? state)
    {
        try
        {
            var threshold = DateTime.UtcNow.AddMinutes(-10);
            foreach (var kvp in _buckets)
            {
                if (kvp.Value.LastUsedUtc < threshold)
                    _buckets.TryRemove(kvp.Key, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rate limiter cleanup error.");
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}
