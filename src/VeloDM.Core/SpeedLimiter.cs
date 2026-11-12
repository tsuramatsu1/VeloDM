using System.Diagnostics;

namespace VeloDM.Core;

/// <summary>
/// Global token bucket shared by every connection. Callers report bytes after reading
/// them; when the bucket is in debt they wait until it refills.
/// </summary>
public sealed class SpeedLimiter
{
    private readonly object _lock = new();
    private double _tokens;
    private long _lastTimestamp = Stopwatch.GetTimestamp();

    /// <summary>Bytes per second; 0 or less means unlimited.</summary>
    public long Limit { get; set; }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        bool charged = false;
        while (true)
        {
            long limit = Limit;
            if (limit <= 0) return;
            TimeSpan delay;
            lock (_lock)
            {
                long now = Stopwatch.GetTimestamp();
                double elapsed = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
                _lastTimestamp = now;
                _tokens = Math.Min(_tokens + elapsed * limit, limit); // at most 1s of burst
                if (!charged) { _tokens -= bytes; charged = true; }
                if (_tokens >= 0) return;
                delay = TimeSpan.FromSeconds(-_tokens / limit);
            }
            await Task.Delay(delay < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : delay, ct)
                .ConfigureAwait(false);
        }
    }
}
