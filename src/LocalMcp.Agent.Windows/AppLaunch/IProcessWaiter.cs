using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.AppLaunch;

public interface IProcessWaiter
{
    Task<CommandResult<ProcessWaitResult>> WaitAsync(
        int? processId,
        string? processName,
        int occurrenceIndex,
        string condition,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken);
}
