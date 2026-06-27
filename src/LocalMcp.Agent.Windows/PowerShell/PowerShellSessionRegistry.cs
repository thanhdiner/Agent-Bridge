using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LocalMcp.Agent.Windows.PowerShell;

/// <summary>
/// Manages the lifecycle of all async PowerShell sessions for this Agent instance.
/// Enforces concurrency and history limits; performs TTL-based expiry of old sessions.
/// Thread-safe; all public methods may be called concurrently.
/// </summary>
internal sealed class PowerShellSessionRegistry : IPowerShellSessionCoordinator, IDisposable
{
    internal const int MaxConcurrentSessions = 16;
    internal const int MaxHistoricalSessions = 100;
    internal static readonly TimeSpan HistoryTtl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, PowerShellSessionState> _sessions = new();
    private readonly ILogger<PowerShellSessionRegistry> _logger;
    private readonly object _cleanupLock = new();
    private bool _disposed;

    public PowerShellSessionRegistry(ILogger<PowerShellSessionRegistry> logger)
    {
        _logger = logger;
    }

    // ── Session creation ──────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to register a new session.
    /// Returns null and populates <paramref name="error"/> when limits are exceeded.
    /// </summary>
    public PowerShellSessionState? TryCreate(
        string deviceId,
        int maxOutputBytes,
        out string? error)
    {
        CleanupExpired();

        var running = CountRunning();
        if (running >= MaxConcurrentSessions)
        {
            error = $"The agent has reached the maximum of {MaxConcurrentSessions} concurrent PowerShell sessions. Cancel or wait for existing sessions to complete.";
            return null;
        }

        var total = _sessions.Count;
        if (total >= MaxHistoricalSessions)
        {
            // Try to evict finished sessions first
            EvictOldestTerminated();
            if (_sessions.Count >= MaxHistoricalSessions)
            {
                error = $"The agent has reached the maximum of {MaxHistoricalSessions} total sessions. Wait for sessions to expire.";
                return null;
            }
        }

        var session = new PowerShellSessionState(Guid.NewGuid(), deviceId, maxOutputBytes);
        _sessions[session.SessionId] = session;
        error = null;
        return session;
    }

    // ── Session lookup ────────────────────────────────────────────────────────

    /// <summary>Returns the session or null if it does not exist.</summary>
    public PowerShellSessionState? Get(Guid sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Cancels all running sessions. Called on Agent shutdown.
    /// </summary>
    public void CancelAll()
    {
        _logger.LogInformation("Cancelling all active PowerShell sessions on shutdown.");
        foreach (var session in _sessions.Values)
        {
            if (session.State == PowerShellSessionStateValue.Running)
            {
                TryCancelSession(session);
            }
        }
    }

    /// <summary>
    /// Cancels a specific session. Idempotent — safe to call on terminal sessions.
    /// </summary>
    public void Cancel(PowerShellSessionState session)
    {
        if (session.State == PowerShellSessionStateValue.Running)
        {
            TryCancelSession(session);
        }
    }

    private static void TryCancelSession(PowerShellSessionState session)
    {
        try
        {
            session.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed — race with natural completion; ignore.
        }
    }

    // ── TTL expiry ────────────────────────────────────────────────────────────

    /// <summary>Called internally to remove expired historical sessions.</summary>
    private void CleanupExpired()
    {
        if (!Monitor.TryEnter(_cleanupLock))
            return; // another thread is already cleaning up

        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (id, session) in _sessions)
            {
                if (session.IsExpired(now))
                {
                    _sessions.TryRemove(id, out _);
                    _logger.LogDebug("Removed expired session {SessionId}", id);
                }
            }
        }
        finally
        {
            Monitor.Exit(_cleanupLock);
        }
    }

    /// <summary>
    /// Called internally from <see cref="PowerShellSessionExecutor"/> when a session
    /// reaches a terminal state, to set its TTL expiry.
    /// </summary>
    public void OnSessionTerminated(PowerShellSessionState session)
    {
        session.SetExpiry(DateTimeOffset.UtcNow.Add(HistoryTtl));
        _logger.LogDebug(
            "Session {SessionId} terminated with state {State}; expires at {Expiry}",
            session.SessionId,
            session.State,
            DateTimeOffset.UtcNow.Add(HistoryTtl));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int CountRunning() =>
        _sessions.Values.Count(s => s.State == PowerShellSessionStateValue.Running);

    private void EvictOldestTerminated()
    {
        var terminated = _sessions.Values
            .Where(s => s.State != PowerShellSessionStateValue.Running)
            .OrderBy(s => s.StartedAt)
            .FirstOrDefault();

        if (terminated is not null)
            _sessions.TryRemove(terminated.SessionId, out _);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAll();
    }
}
