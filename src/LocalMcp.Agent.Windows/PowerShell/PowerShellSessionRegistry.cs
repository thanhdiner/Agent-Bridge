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

    public void CancelAll()
    {
        lock (_registryLock)
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
        try
        {
            session.Cts.Cancel();
        }
        catch (ObjectDisposedException) {}
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
        lock (_registryLock)
        {
            if (_disposed) return;
            _disposed = true;
            CancelAll();
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
            _sessions.Clear();
        }
    }
}
