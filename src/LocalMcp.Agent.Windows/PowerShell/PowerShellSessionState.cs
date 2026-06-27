using System.Diagnostics;

namespace LocalMcp.Agent.Windows.PowerShell;

/// <summary>
/// Possible lifecycle states for an async PowerShell session.
/// Values are stored as int for atomic compare-exchange transitions.
/// </summary>
internal enum PowerShellSessionStateValue
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
    TimedOut = 4
}

/// <summary>
/// Holds all mutable state for a single async PowerShell session.
/// Thread-safety: state transitions use Interlocked.CompareExchange;
/// output buffer access uses <see cref="_outputLock"/>.
/// </summary>
internal sealed class PowerShellSessionState
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public Guid SessionId { get; }
    public string DeviceId { get; }
    public DateTimeOffset StartedAt { get; }

    // ── Lifecycle state (atomic) ──────────────────────────────────────────────

    // Underlying int for Interlocked CAS. Read via State property.
    private int _stateValue = (int)PowerShellSessionStateValue.Running;
    private DateTimeOffset? _completedAt;
    private int? _exitCode;

    // ── Output buffers (guarded by _outputLock) ───────────────────────────────

    private readonly object _outputLock = new();
    private readonly byte[] _stdoutBuffer;
    private readonly byte[] _stderrBuffer;
    private int _stdoutLength;
    private int _stderrLength;
    private bool _stdoutTruncated;
    private bool _stderrTruncated;

    // ── Cancellation / process reference ─────────────────────────────────────

    public CancellationTokenSource Cts { get; } = new();
    // Set immediately after process.Start(); used for process-tree kill.
    public Process? Process { get; set; }

    // ── Construction ─────────────────────────────────────────────────────────

    public PowerShellSessionState(Guid sessionId, string deviceId, int maxOutputBytes)
    {
        SessionId = sessionId;
        DeviceId = deviceId;
        StartedAt = DateTimeOffset.UtcNow;

        // Allocate bounded buffers immediately. Output is capped at maxOutputBytes
        // total (half each by default, then bias unused half to the other side).
        var half = maxOutputBytes / 2;
        _stdoutBuffer = new byte[half];
        _stderrBuffer = new byte[maxOutputBytes - half];
    }

    // ── State transitions ─────────────────────────────────────────────────────

    public PowerShellSessionStateValue State =>
        (PowerShellSessionStateValue)Volatile.Read(ref _stateValue);

    public DateTimeOffset? CompletedAt => _completedAt;
    public int? ExitCode => _exitCode;

    /// <summary>
    /// Attempt an atomic transition from <see cref="PowerShellSessionStateValue.Running"/>
    /// to <paramref name="newState"/>. Returns true if this call won the race.
    /// A session that has already terminated cannot be transitioned again.
    /// </summary>
    public bool TryTransition(PowerShellSessionStateValue newState, int? exitCode = null)
    {
        var original = Interlocked.CompareExchange(
            ref _stateValue,
            (int)newState,
            (int)PowerShellSessionStateValue.Running);

        if (original != (int)PowerShellSessionStateValue.Running)
            return false; // already terminal

        _exitCode = exitCode;
        _completedAt = DateTimeOffset.UtcNow;
        return true;
    }

    // ── Output accumulation ───────────────────────────────────────────────────

    /// <summary>
    /// Appends bytes to the stdout buffer, capping at the allocated size.
    /// Thread-safe; called from the background reader task.
    /// </summary>
    public void AppendStdout(byte[] data, int count)
    {
        lock (_outputLock)
        {
            AppendToBuffer(_stdoutBuffer, ref _stdoutLength, ref _stdoutTruncated, data, count);
        }
    }

    /// <summary>
    /// Appends bytes to the stderr buffer, capping at the allocated size.
    /// Thread-safe; called from the background reader task.
    /// </summary>
    public void AppendStderr(byte[] data, int count)
    {
        lock (_outputLock)
        {
            AppendToBuffer(_stderrBuffer, ref _stderrLength, ref _stderrTruncated, data, count);
        }
    }

    private static void AppendToBuffer(
        byte[] buffer,
        ref int length,
        ref bool truncated,
        byte[] data,
        int count)
    {
        var remaining = buffer.Length - length;
        if (remaining <= 0)
        {
            truncated = true;
            return;
        }

        var toCopy = Math.Min(count, remaining);
        Buffer.BlockCopy(data, 0, buffer, length, toCopy);
        length += toCopy;
        if (toCopy < count)
            truncated = true;
    }

    /// <summary>
    /// Reads an incremental slice of stdout/stderr starting at <paramref name="byteOffset"/>,
    /// returning at most <paramref name="maxBytes"/> bytes total across both streams.
    /// Thread-safe snapshot; reads are non-destructive (offset-based).
    /// </summary>
    public OutputSnapshot ReadOutput(long byteOffset, int maxBytes)
    {
        lock (_outputLock)
        {
            // stdout slice
            var stdoutAvail = Math.Max(0L, _stdoutLength - byteOffset);
            var stdoutSliceLen = (int)Math.Min(stdoutAvail, maxBytes / 2);
            var stdoutSlice = stdoutSliceLen > 0
                ? new byte[stdoutSliceLen]
                : [];
            if (stdoutSliceLen > 0)
                Buffer.BlockCopy(_stdoutBuffer, (int)byteOffset, stdoutSlice, 0, stdoutSliceLen);

            // stderr slice — uses same offset since they're independent buffers
            var stderrAvail = Math.Max(0L, _stderrLength - byteOffset);
            var stderrBudget = maxBytes - stdoutSliceLen;
            var stderrSliceLen = (int)Math.Min(stderrAvail, stderrBudget);
            var stderrSlice = stderrSliceLen > 0
                ? new byte[stderrSliceLen]
                : [];
            if (stderrSliceLen > 0)
                Buffer.BlockCopy(_stderrBuffer, (int)byteOffset, stderrSlice, 0, stderrSliceLen);

            // nextOffset advances by the larger of the two slices
            var advance = (long)Math.Max(stdoutSliceLen, stderrSliceLen);
            var nextOffset = byteOffset + advance;

            return new OutputSnapshot(
                StdoutBytes: stdoutSlice,
                StderrBytes: stderrSlice,
                NextOffset: nextOffset,
                StdoutTruncated: _stdoutTruncated,
                StderrTruncated: _stderrTruncated);
        }
    }

    // ── TTL (for historical cleanup) ──────────────────────────────────────────

    private DateTimeOffset? _expiry;

    /// <summary>Sets TTL expiry once the session reaches a terminal state.</summary>
    public void SetExpiry(DateTimeOffset expiry) => _expiry = expiry;

    /// <summary>True when a historical TTL has been set and has elapsed.</summary>
    public bool IsExpired(DateTimeOffset now) =>
        _expiry.HasValue && now >= _expiry.Value;
}

internal sealed record OutputSnapshot(
    byte[] StdoutBytes,
    byte[] StderrBytes,
    long NextOffset,
    bool StdoutTruncated,
    bool StderrTruncated);
