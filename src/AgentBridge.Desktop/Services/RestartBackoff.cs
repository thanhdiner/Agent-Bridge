using System;

namespace AgentBridge.Desktop.Services;

public sealed class RestartBackoff
{
    private static readonly TimeSpan[] Delays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    };

    private readonly TimeSpan _stablePeriod;
    private DateTimeOffset? _lastStartedUtc;
    private DateTimeOffset _nextRetryUtc = DateTimeOffset.MinValue;

    public RestartBackoff(TimeSpan? stablePeriod = null)
    {
        _stablePeriod = stablePeriod ?? TimeSpan.FromSeconds(30);
    }

    public int FailureCount { get; private set; }

    public DateTimeOffset NextRetryUtc => _nextRetryUtc;

    public void RecordStarted(DateTimeOffset now)
    {
        _lastStartedUtc = now;
    }

    public TimeSpan RecordFailure(DateTimeOffset now)
    {
        FailureCount++;
        var delay = Delays[Math.Min(FailureCount - 1, Delays.Length - 1)];
        _nextRetryUtc = now + delay;
        _lastStartedUtc = null;
        return delay;
    }

    public void ObserveHealthy(DateTimeOffset now)
    {
        if (_lastStartedUtc is not null && now - _lastStartedUtc.Value >= _stablePeriod)
            Reset();
    }

    public bool CanStart(DateTimeOffset now, out TimeSpan remaining)
    {
        if (now >= _nextRetryUtc)
        {
            remaining = TimeSpan.Zero;
            return true;
        }

        remaining = _nextRetryUtc - now;
        return false;
    }

    public void Reset()
    {
        FailureCount = 0;
        _lastStartedUtc = null;
        _nextRetryUtc = DateTimeOffset.MinValue;
    }
}
