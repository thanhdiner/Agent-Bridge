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
    private readonly byte[] _stdoutBuffer;
    private readonly byte[] _stderrBuffer;
    private int _stdoutLength;
    private int _stderrLength;
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
        _stdoutBuffer = new byte[maxOutputBytes];
        _stderrBuffer = new byte[maxOutputBytes];
    }

    public PowerShellSessionSnapshot GetSnapshot() => _snapshot;

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
            var currentTotal = _stdoutLength + _stderrLength;
            if (currentTotal >= _maxOutputBytes)
            {
                _truncated = true;
                return;
            }
            var spaceLeft = _maxOutputBytes - currentTotal;
            var toCopy = Math.Min(count, spaceLeft);
            if (toCopy > 0)
            {
                Buffer.BlockCopy(data, 0, _stdoutBuffer, _stdoutLength, toCopy);
                _stdoutLength += toCopy;
            }
            if (toCopy < count)
            {
                _truncated = true;
                _stdoutLength = CleanTrailingUtf8Rune(_stdoutBuffer, _stdoutLength);
            }
        }
    }

    public void AppendStderr(byte[] data, int count)
    {
        lock (_outputLock)
        {
            var currentTotal = _stdoutLength + _stderrLength;
            if (currentTotal >= _maxOutputBytes)
            {
                _truncated = true;
                return;
            }
            var spaceLeft = _maxOutputBytes - currentTotal;
            var toCopy = Math.Min(count, spaceLeft);
            if (toCopy > 0)
            {
                Buffer.BlockCopy(data, 0, _stderrBuffer, _stderrLength, toCopy);
                _stderrLength += toCopy;
            }
            if (toCopy < count)
            {
                _truncated = true;
                _stderrLength = CleanTrailingUtf8Rune(_stderrBuffer, _stderrLength);
            }
        }
    }

    public OutputSnapshot ReadOutput(long stdoutOffset, long stderrOffset, int maxBytes)
    {
        lock (_outputLock)
        {
            // Normalize offsets to int range and make sure they don't exceed current length
            int stdOffset = (int)Math.Clamp(stdoutOffset, 0, _stdoutLength);
            int errOffset = (int)Math.Clamp(stderrOffset, 0, _stderrLength);

            // Align offsets if they point inside a multi-byte UTF-8 sequence
            stdOffset = NormalizeOffsetToUtf8Boundary(_stdoutBuffer, _stdoutLength, stdOffset);
            errOffset = NormalizeOffsetToUtf8Boundary(_stderrBuffer, _stderrLength, errOffset);

            var stdoutAvail = _stdoutLength - stdOffset;
            var stdoutBudget = Math.Min(stdoutAvail, maxBytes);
            var stdoutLen = GetSafeUtf8Length(_stdoutBuffer, _stdoutLength, stdOffset, stdoutBudget);
            
            var stdoutSlice = new byte[stdoutLen];
            if (stdoutLen > 0)
                Buffer.BlockCopy(_stdoutBuffer, stdOffset, stdoutSlice, 0, stdoutLen);

            var stderrAvail = _stderrLength - errOffset;
            var stderrBudget = Math.Min(stderrAvail, maxBytes - stdoutLen);
            var stderrLen = GetSafeUtf8Length(_stderrBuffer, _stderrLength, errOffset, stderrBudget);

            var stderrSlice = new byte[stderrLen];
            if (stderrLen > 0)
                Buffer.BlockCopy(_stderrBuffer, errOffset, stderrSlice, 0, stderrLen);

            return new OutputSnapshot(
                StdoutBytes: stdoutSlice,
                StderrBytes: stderrSlice,
                NextStdoutOffset: stdOffset + stdoutLen,
                NextStderrOffset: errOffset + stderrLen,
                Truncated: _truncated);
        }
    }

    private static int NormalizeOffsetToUtf8Boundary(byte[] buffer, int length, int offset)
    {
        if (offset <= 0 || offset >= length)
            return offset;

        int current = offset;
        while (current >= 0 && current > offset - 4)
        {
            byte b = buffer[current];
            if (b < 128)
            {
                return offset;
            }
            if (b >= 192)
            {
                return current;
            }
            current--;
        }
        return offset;
    }

    private static int CleanTrailingUtf8Rune(byte[] buffer, int length)
    {
        for (int i = length - 1; i >= 0 && i >= length - 4; i--)
        {
            byte b = buffer[i];
            if (b < 128)
                break;
            if (b >= 192)
            {
                int expected = 1;
                if (b >= 240) expected = 4;
                else if (b >= 224) expected = 3;
                else if (b >= 192) expected = 2;

                if (length - i < expected)
                {
                    return i;
                }
                break;
            }
        }
        return length;
    }

    private static int GetSafeUtf8Length(byte[] buffer, int length, int start, int requestedLength)
    {
        if (start >= length)
            return 0;

        if (start + requestedLength >= length)
        {
            return length - start;
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
                    return i - start;
                }
                break;
            }
        }
        return requestedLength;
    }

    public void SignalCancel()
    {
        try { Cts.Cancel(); } catch {}
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
