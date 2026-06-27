using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LocalMcp.Agent.Windows.PowerShell;

internal sealed class PowerShellSessionRegistry : IPowerShellSessionCoordinator, IDisposable
{
    internal const int MaxConcurrentSessions = 16;
    internal const int MaxHistoricalSessions = 100;
    internal static readonly TimeSpan HistoryTtl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, PowerShellSessionState> _sessions = new();
    private readonly object _registryLock = new();
    private readonly ILogger<PowerShellSessionRegistry> _logger;
    private bool _disposed;

    internal int Count
    {
        get
        {
            lock (_registryLock) return _sessions.Count;
        }
    }

    internal IReadOnlyCollection<PowerShellSessionState> Sessions
    {
        get
        {
            lock (_registryLock) return _sessions.Values.ToArray();
        }
    }

    public PowerShellSessionRegistry(ILogger<PowerShellSessionRegistry> logger)
    {
        _logger = logger;
    }

    public PowerShellSessionState? TryCreate(
        string deviceId,
        int maxOutputBytes,
        out string? error)
    {
        lock (_registryLock)
        {
            if (_disposed)
            {
                error = "Registry is disposed.";
                return null;
            }

            CleanupExpiredLocked();

            var running = _sessions.Values.Count(s => s.State == PowerShellSessionStateValue.Running);
            if (running >= MaxConcurrentSessions)
            {
                error = $"The agent has reached the maximum of {MaxConcurrentSessions} concurrent PowerShell sessions. Cancel or wait for existing sessions to complete.";
                return null;
            }

            if (_sessions.Count >= MaxHistoricalSessions)
            {
                EvictOldestTerminatedLocked();
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
    }

    public PowerShellSessionState? Get(Guid sessionId)
    {
        lock (_registryLock)
        {
            if (_disposed) return null;

            if (_sessions.TryGetValue(sessionId, out var session))
            {
                if (session.IsExpired(DateTimeOffset.UtcNow))
                {
                    _sessions.TryRemove(sessionId, out _);
                    session.Dispose();
                    return null;
                }
                return session;
            }
            return null;
        }
    }

    public bool Remove(Guid sessionId)
    {
        lock (_registryLock)
        {
            if (_sessions.TryRemove(sessionId, out var session))
            {
                session.Dispose();
                return true;
            }
            return false;
        }
    }

    public void CancelAll()
    {
        List<PowerShellSessionState> runningSessions;
        lock (_registryLock)
        {
            _logger.LogInformation("Cancelling all active PowerShell sessions on shutdown.");
            runningSessions = _sessions.Values.Where(s => s.State == PowerShellSessionStateValue.Running).ToList();
            foreach (var session in runningSessions)
            {
                TryCancelSession(session);
            }
        }

        // Bounded wait up to 5s for running sessions to cleanly stop
        if (runningSessions.Count > 0)
        {
            bool cleanExit = false;
            try
            {
                cleanExit = Task.WhenAll(runningSessions.Select(s => s.CompletionTask)).Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Timeout or error waiting for running sessions to stop during shutdown.");
            }

            if (!cleanExit)
            {
                _logger.LogWarning("Some sessions did not exit cleanly during shutdown. Force killing remaining processes.");

                // Force kill entire process tree for any session that hasn't finished
                foreach (var session in runningSessions)
                {
                    var proc = session.Process;
                    if (proc != null)
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                _logger.LogInformation("Force killing process tree for session {SessionId}.", session.SessionId);
                                proc.Kill(entireProcessTree: true);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to force kill process tree for session {SessionId}.", session.SessionId);
                        }
                    }
                }

                // Final bounded wait for killed processes to exit
                try
                {
                    Task.WhenAll(runningSessions.Select(s => s.CompletionTask)).Wait(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Timeout during final wait for forced kill processes.");
                }

                // Check and log critical if still running
                foreach (var session in runningSessions)
                {
                    var proc = session.Process;
                    if (proc != null)
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                _logger.LogCritical("CRITICAL: Process tree for session {SessionId} (PID {Pid}) failed to exit after forced kill.", session.SessionId, proc.Id);
                            }
                        }
                        catch {}
                    }
                }
            }
        }
    }

    public void Cancel(PowerShellSessionState session)
    {
        lock (_registryLock)
        {
            if (session.State == PowerShellSessionStateValue.Running)
            {
                TryCancelSession(session);
            }
        }
    }

    private static void TryCancelSession(PowerShellSessionState session)
    {
        session.SignalCancel();
    }

    public void OnSessionTerminated(PowerShellSessionState session)
    {
        lock (_registryLock)
        {
            if (_disposed) return;
            session.SetExpiry(DateTimeOffset.UtcNow.Add(HistoryTtl));
            _logger.LogDebug(
                "Session {SessionId} terminated with state {State}; expires at {Expiry}",
                session.SessionId,
                session.State,
                DateTimeOffset.UtcNow.Add(HistoryTtl));
        }
    }

    private void CleanupExpiredLocked()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, session) in _sessions)
        {
            if (session.IsExpired(now))
            {
                _sessions.TryRemove(id, out _);
                session.Dispose();
                _logger.LogDebug("Removed expired session {SessionId}", id);
            }
        }
    }

    private void EvictOldestTerminatedLocked()
    {
        var terminated = _sessions.Values
            .Where(s => s.State != PowerShellSessionStateValue.Running)
            .OrderBy(s => s.StartedAt)
            .FirstOrDefault();

        if (terminated is not null)
        {
            _sessions.TryRemove(terminated.SessionId, out _);
            terminated.Dispose();
        }
    }

    public void Dispose()
    {
        bool performCleanup = false;
        lock (_registryLock)
        {
            if (!_disposed)
            {
                _disposed = true;
                performCleanup = true;
            }
        }

        if (performCleanup)
        {
            CancelAll();
            lock (_registryLock)
            {
                foreach (var session in _sessions.Values)
                {
                    session.Dispose();
                }
                _sessions.Clear();
            }
        }
    }
}
