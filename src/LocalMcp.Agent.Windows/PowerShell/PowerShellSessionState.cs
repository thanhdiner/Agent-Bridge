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
    private byte[]? _stdoutBuffer;
    private byte[]? _stderrBuffer;
    private int _stdoutLength;
    private int _stderrLength;
    private readonly int _maxOutputBytes;
    private bool _truncated;
    private byte[] _stdoutPending = Array.Empty<byte>();
    private byte[] _stderrPending = Array.Empty<byte>();

    internal int AllocatedStdoutCapacity => _stdoutBuffer?.Length ?? 0;
    internal int AllocatedStderrCapacity => _stderrBuffer?.Length ?? 0;

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
        bool transitioned = false;
        lock (_stateLock)
        {
            if (_snapshot.State == PowerShellSessionStateValue.Running)
            {
                _snapshot = new PowerShellSessionSnapshot(newState, DateTimeOffset.UtcNow, exitCode);
                _completionSource.TrySetResult();
                transitioned = true;
            }
        }

        if (transitioned)
        {
            FlushPendingBytes();
        }
        return transitioned;
    }

    public void AppendStdout(byte[] data, int count)
    {
        lock (_outputLock)
        {
            if (_truncated)
                return;

            byte[] combined;
            if (_stdoutPending.Length > 0)
            {
                combined = new byte[_stdoutPending.Length + count];
                Buffer.BlockCopy(_stdoutPending, 0, combined, 0, _stdoutPending.Length);
                Buffer.BlockCopy(data, 0, combined, _stdoutPending.Length, count);
            }
            else
            {
                combined = new byte[count];
                Buffer.BlockCopy(data, 0, combined, 0, count);
            }

            int completeLength = GetCompleteUtf8Length(combined, combined.Length);

            if (completeLength > 0)
            {
                var currentTotal = _stdoutLength + _stderrLength;
                var spaceLeft = _maxOutputBytes - currentTotal;

                if (completeLength > spaceLeft)
                {
                    _truncated = true;
                    var allowedBytes = GetSafeUtf8Length(combined, completeLength, 0, spaceLeft);
                    if (allowedBytes > 0)
                    {
                        EnsureStdoutCapacity(_stdoutLength + allowedBytes);
                        Buffer.BlockCopy(combined, 0, _stdoutBuffer!, _stdoutLength, allowedBytes);
                        _stdoutLength += allowedBytes;
                    }
                    _stdoutPending = Array.Empty<byte>();
                    return;
                }

                EnsureStdoutCapacity(_stdoutLength + completeLength);
                Buffer.BlockCopy(combined, 0, _stdoutBuffer!, _stdoutLength, completeLength);
                _stdoutLength += completeLength;
            }

            if (completeLength < combined.Length)
            {
                int pendingLen = combined.Length - completeLength;
                _stdoutPending = new byte[pendingLen];
                Buffer.BlockCopy(combined, completeLength, _stdoutPending, 0, pendingLen);
            }
            else
            {
                _stdoutPending = Array.Empty<byte>();
            }
        }
    }

    public void AppendStderr(byte[] data, int count)
    {
        lock (_outputLock)
        {
            if (_truncated)
                return;

            byte[] combined;
            if (_stderrPending.Length > 0)
            {
                combined = new byte[_stderrPending.Length + count];
                Buffer.BlockCopy(_stderrPending, 0, combined, 0, _stderrPending.Length);
                Buffer.BlockCopy(data, 0, combined, _stderrPending.Length, count);
            }
            else
            {
                combined = new byte[count];
                Buffer.BlockCopy(data, 0, combined, 0, count);
            }

            int completeLength = GetCompleteUtf8Length(combined, combined.Length);

            if (completeLength > 0)
            {
                var currentTotal = _stdoutLength + _stderrLength;
                var spaceLeft = _maxOutputBytes - currentTotal;

                if (completeLength > spaceLeft)
                {
                    _truncated = true;
                    var allowedBytes = GetSafeUtf8Length(combined, completeLength, 0, spaceLeft);
                    if (allowedBytes > 0)
                    {
                        EnsureStderrCapacity(_stderrLength + allowedBytes);
                        Buffer.BlockCopy(combined, 0, _stderrBuffer!, _stderrLength, allowedBytes);
                        _stderrLength += allowedBytes;
                    }
                    _stderrPending = Array.Empty<byte>();
                    return;
                }

                EnsureStderrCapacity(_stderrLength + completeLength);
                Buffer.BlockCopy(combined, 0, _stderrBuffer!, _stderrLength, completeLength);
                _stderrLength += completeLength;
            }

            if (completeLength < combined.Length)
            {
                int pendingLen = combined.Length - completeLength;
                _stderrPending = new byte[pendingLen];
                Buffer.BlockCopy(combined, completeLength, _stderrPending, 0, pendingLen);
            }
            else
            {
                _stderrPending = Array.Empty<byte>();
            }
        }
    }

    public OutputSnapshot ReadOutput(long stdoutOffset, long stderrOffset, int maxBytes)
    {
        lock (_outputLock)
        {
            int stdOffset = (int)Math.Clamp(stdoutOffset, 0, _stdoutLength);
            int errOffset = (int)Math.Clamp(stderrOffset, 0, _stderrLength);

            stdOffset = NormalizeOffsetToUtf8Boundary(_stdoutBuffer, _stdoutLength, stdOffset);
            errOffset = NormalizeOffsetToUtf8Boundary(_stderrBuffer, _stderrLength, errOffset);

            var stdoutAvail = _stdoutLength - stdOffset;
            var stdoutBudget = Math.Min(stdoutAvail, maxBytes);
            var stdoutLen = GetSafeUtf8Length(_stdoutBuffer, _stdoutLength, stdOffset, stdoutBudget);
            
            var stdoutSlice = new byte[stdoutLen];
            if (stdoutLen > 0 && _stdoutBuffer != null)
                Buffer.BlockCopy(_stdoutBuffer, stdOffset, stdoutSlice, 0, stdoutLen);

            var stderrAvail = _stderrLength - errOffset;
            var stderrBudget = Math.Min(stderrAvail, maxBytes - stdoutLen);
            var stderrLen = GetSafeUtf8Length(_stderrBuffer, _stderrLength, errOffset, stderrBudget);

            var stderrSlice = new byte[stderrLen];
            if (stderrLen > 0 && _stderrBuffer != null)
                Buffer.BlockCopy(_stderrBuffer, errOffset, stderrSlice, 0, stderrLen);

            return new OutputSnapshot(
                StdoutBytes: stdoutSlice,
                StderrBytes: stderrSlice,
                NextStdoutOffset: stdOffset + stdoutLen,
                NextStderrOffset: errOffset + stderrLen,
                Truncated: _truncated);
        }
    }

    private void EnsureStdoutCapacity(int requiredLength)
    {
        ShrinkBufferToLength(ref _stderrBuffer, _stderrLength);

        int currentStdoutCap = _stdoutBuffer?.Length ?? 0;
        if (requiredLength <= currentStdoutCap)
            return;

        int currentStderrCap = _stderrBuffer?.Length ?? 0;
        int maxAllowedStdoutCap = _maxOutputBytes - currentStderrCap;

        int newCap = Math.Max(1024, requiredLength);
        if (_stdoutBuffer != null)
        {
            newCap = Math.Max(_stdoutBuffer.Length * 2, newCap);
        }

        if (newCap > maxAllowedStdoutCap)
        {
            newCap = maxAllowedStdoutCap;
        }

        if (newCap < requiredLength)
        {
            newCap = requiredLength;
        }

        var newBuffer = new byte[newCap];
        if (_stdoutBuffer != null && _stdoutLength > 0)
        {
            Buffer.BlockCopy(_stdoutBuffer, 0, newBuffer, 0, _stdoutLength);
        }
        _stdoutBuffer = newBuffer;
    }

    private void EnsureStderrCapacity(int requiredLength)
    {
        ShrinkBufferToLength(ref _stdoutBuffer, _stdoutLength);

        int currentStderrCap = _stderrBuffer?.Length ?? 0;
        if (requiredLength <= currentStderrCap)
            return;

        int currentStdoutCap = _stdoutBuffer?.Length ?? 0;
        int maxAllowedStderrCap = _maxOutputBytes - currentStdoutCap;

        int newCap = Math.Max(1024, requiredLength);
        if (_stderrBuffer != null)
        {
            newCap = Math.Max(_stderrBuffer.Length * 2, newCap);
        }

        if (newCap > maxAllowedStderrCap)
        {
            newCap = maxAllowedStderrCap;
        }

        if (newCap < requiredLength)
        {
            newCap = requiredLength;
        }

        var newBuffer = new byte[newCap];
        if (_stderrBuffer != null && _stderrLength > 0)
        {
            Buffer.BlockCopy(_stderrBuffer, 0, newBuffer, 0, _stderrLength);
        }
        _stderrBuffer = newBuffer;
    }

    private static void ShrinkBufferToLength(ref byte[]? buffer, int length)
    {
        if (buffer == null) return;
        if (buffer.Length > length)
        {
            if (length == 0)
            {
                buffer = null;
            }
            else
            {
                var newBuffer = new byte[length];
                Buffer.BlockCopy(buffer, 0, newBuffer, 0, length);
                buffer = newBuffer;
            }
        }
    }

    private void FlushPendingBytes()
    {
        lock (_outputLock)
        {
            if (_truncated)
                return;

            if (_stdoutPending.Length > 0)
            {
                var spaceLeft = _maxOutputBytes - (_stdoutLength + _stderrLength);
                var toCopy = Math.Min(_stdoutPending.Length, spaceLeft);
                if (toCopy > 0)
                {
                    EnsureStdoutCapacity(_stdoutLength + toCopy);
                    Buffer.BlockCopy(_stdoutPending, 0, _stdoutBuffer!, _stdoutLength, toCopy);
                    _stdoutLength += toCopy;
                }
                if (toCopy < _stdoutPending.Length)
                {
                    _truncated = true;
                }
                _stdoutPending = Array.Empty<byte>();
            }

            if (_stderrPending.Length > 0)
            {
                var spaceLeft = _maxOutputBytes - (_stdoutLength + _stderrLength);
                var toCopy = Math.Min(_stderrPending.Length, spaceLeft);
                if (toCopy > 0)
                {
                    EnsureStderrCapacity(_stderrLength + toCopy);
                    Buffer.BlockCopy(_stderrPending, 0, _stderrBuffer!, _stderrLength, toCopy);
                    _stderrLength += toCopy;
                }
                if (toCopy < _stderrPending.Length)
                {
                    _truncated = true;
                }
                _stderrPending = Array.Empty<byte>();
            }
        }
    }

    private static int GetCompleteUtf8Length(byte[] buffer, int length)
    {
        for (int i = length - 1; i >= 0 && i >= length - 4; i--)
        {
            byte b = buffer[i];
            if (b < 128)
            {
                break;
            }
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

    private static int NormalizeOffsetToUtf8Boundary(byte[]? buffer, int length, int offset)
    {
        if (buffer == null || offset <= 0 || offset >= length)
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

    private static int GetSafeUtf8Length(byte[]? buffer, int length, int start, int requestedLength)
    {
        if (buffer == null || start >= length)
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
