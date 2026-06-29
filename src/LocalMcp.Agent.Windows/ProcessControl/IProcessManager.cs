using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.ProcessControl;

public interface IProcessManager
{
    Task<CommandResult<ProcessListResult>> ListAsync(
        string? nameContains,
        bool includeWindowless,
        int maxResults,
        Guid commandId,
        CancellationToken cancellationToken);

    Task<CommandResult<ProcessKillResult>> KillAsync(
        int processId,
        string? expectedProcessName,
        bool entireProcessTree,
        int timeoutMs,
        Guid commandId,
        CancellationToken cancellationToken);
}
