using System.Diagnostics;

namespace LocalMcp.Agent.Windows.PowerShell;

public enum PowerShellSessionStateValue
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
    TimedOut = 4
}

internal sealed class PowerShellSessionSnapshot
{
    public PowerShellSessionStateValue State { get; }
    public DateTimeOffset? CompletedAt { get; }
    public int? ExitCode { get; }

    public PowerShellSessionSnapshot(PowerShellSessionStateValue state, DateTimeOffset? completedAt, int? exitCode)
    {
        State = state;
        CompletedAt = completedAt;
        ExitCode = exitCode;
    }
}

internal sealed record OutputSnapshot(
    byte[] StdoutBytes,
    byte[] StderrBytes,
    long NextStdoutOffset,
    long NextStderrOffset,
    bool Truncated);

internal sealed class PowerShellSessionState : IDisposable
{
    public Guid SessionId { get; }
    public string DeviceId { get; }
    public DateTimeOffset StartedAt { get; }

    private readonly object _stateLock = new();
    private volatile PowerShellSessionSnapshot _snapshot = new(PowerShellSessionStateValue.Running, null, null);

    private readonly object _outputLock = new();
    private readonly List<byte> _stdout = new();
    private readonly List<byte> _stderr = new();
    private readonly int _maxOutputBytes;
    private bool _truncated;

    public CancellationTokenSource Cts { get; } = new();

    private readonly object _processLock = new();
    private Process? _process;
    private bool _disposed;

    public Task CompletionTask => _completionSource.Task;
    private readonly TaskCompletionSource _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DateTimeOffset? _expiry;

    public PowerShellSessionState(Guid sessionId, string deviceId, int maxOutputBytes)
    {
        SessionId = sessionId;
        DeviceId = deviceId;
        StartedAt = DateTimeOffset.UtcNow;
        _maxOutputBytes = maxOutputBytes;
    }

    public PowerShellSessionStateValue State => _snapshot.State;
    public DateTimeOffset? CompletedAt => _snapshot.CompletedAt;
    public int? ExitCode => _snapshot.ExitCode;

    public Process? Process
    {
        get
        {
            lock (_processLock) return _process;
        }
        set
        {
            lock (_processLock)
            {
                if (_disposed)
                {
                    value?.Dispose();
                    return;
                }
                _process = value;
            }
        }
    }

    public bool TryTransition(PowerShellSessionStateValue newState, int? exitCode = null)
    {
        lock (_stateLock)
        {
            if (_snapshot.State != PowerShellSessionStateValue.Running)
                return false;

            _snapshot = new PowerShellSessionSnapshot(newState, DateTimeOffset.UtcNow, exitCode);
            _completionSource.TrySetResult();
            return true;
        }
    }

    public void AppendStdout(byte[] data, int count)
    {
        lock (_outputLock)
        {
            var currentTotal = _stdout.Count + _stderr.Count;
            if (currentTotal >= _maxOutputBytes)
            {
                _truncated = true;
                return;
            }
            var spaceLeft = _maxOutputBytes - currentTotal;
            var toCopy = Math.Min(count, spaceLeft);
            for (int i = 0; i < toCopy; i++)
            {
                _stdout.Add(data[i]);
            }
            if (toCopy < count)
            {
                _truncated = true;
            }
        }
    }

    public void AppendStderr(byte[] data, int count)
    {
        lock (_outputLock)
        {
            var currentTotal = _stdout.Count + _stderr.Count;
            if (currentTotal >= _maxOutputBytes)
            {
                _truncated = true;
                return;
            }
            var spaceLeft = _maxOutputBytes - currentTotal;
            var toCopy = Math.Min(count, spaceLeft);
            for (int i = 0; i < toCopy; i++)
            {
                _stderr.Add(data[i]);
            }
            if (toCopy < count)
            {
                _truncated = true;
            }
        }
    }

    public OutputSnapshot ReadOutput(long stdoutOffset, long stderrOffset, int maxBytes)
    {
        lock (_outputLock)
        {
            byte[] stdoutBytes = _stdout.ToArray();
            byte[] stderrBytes = _stderr.ToArray();

            var stdoutAvail = Math.Max(0, stdoutBytes.Length - (int)stdoutOffset);
            var stdoutBudget = Math.Min(stdoutAvail, maxBytes / 2);
            var stdoutLen = GetSafeUtf8Length(stdoutBytes, (int)stdoutOffset, (int)stdoutBudget);

            var stdoutSlice = new byte[stdoutLen];
            if (stdoutLen > 0)
                Buffer.BlockCopy(stdoutBytes, (int)stdoutOffset, stdoutSlice, 0, stdoutLen);

            var stderrAvail = Math.Max(0, stderrBytes.Length - (int)stderrOffset);
            var stderrBudget = Math.Min(stderrAvail, maxBytes - stdoutLen);
            var stderrLen = GetSafeUtf8Length(stderrBytes, (int)stderrOffset, (int)stderrBudget);

            var stderrSlice = new byte[stderrLen];
            if (stderrLen > 0)
                Buffer.BlockCopy(stderrBytes, (int)stderrOffset, stderrSlice, 0, stderrLen);

            return new OutputSnapshot(
                StdoutBytes: stdoutSlice,
                StderrBytes: stderrSlice,
                NextStdoutOffset: stdoutOffset + stdoutLen,
                NextStderrOffset: stderrOffset + stderrLen,
                Truncated: _truncated);
        }
    }

    private static int GetSafeUtf8Length(byte[] buffer, int start, int requestedLength)
    {
        if (start >= buffer.Length)
            return 0;

        if (requestedLength <= 0)
            requestedLength = 1;

        byte startByte = buffer[start];
        if (startByte >= 192)
        {
            int expected = 1;
            if (startByte >= 240) expected = 4;
            else if (startByte >= 224) expected = 3;
            else if (startByte >= 192) expected = 2;

            if (start + expected <= buffer.Length && expected > requestedLength)
            {
                requestedLength = expected;
            }
        }

        if (start + requestedLength >= buffer.Length)
        {
            return buffer.Length - start;
        }

        int end = start + requestedLength;
        for (int i = end - 1; i >= start; i--)
        {
            byte b = buffer[i];
            if (b < 128)
            {
                break;
            }
            if (b >= 192)
            {
                int expectedBytes = 1;
                if (b >= 240) expectedBytes = 4;
                else if (b >= 224) expectedBytes = 3;
                else if (b >= 192) expectedBytes = 2;

                int actualBytes = end - i;
                if (actualBytes < expectedBytes)
                {
                    if (i == start)
                    {
                        if (start + expectedBytes <= buffer.Length)
                        {
                            return expectedBytes;
                        }
                    }
                    return i - start;
                }
                break;
            }
        }
        return requestedLength;
    }

    public void SetExpiry(DateTimeOffset expiry)
    {
        _expiry = expiry;
    }

    public bool IsExpired(DateTimeOffset now)
    {
        return _expiry.HasValue && now >= _expiry.Value;
    }

    public void Dispose()
    {
        lock (_processLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { Cts.Cancel(); } catch {}
        try { Cts.Dispose(); } catch {}

        lock (_processLock)
        {
            try { _process?.Dispose(); } catch {}
            _process = null;
        }
        _completionSource.TrySetResult();
    }
}
