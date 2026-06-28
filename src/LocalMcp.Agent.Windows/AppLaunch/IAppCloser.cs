using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.AppLaunch;

public interface IAppCloser
{
    Task<CommandResult<AppCloseResult>> CloseAsync(
        int? processId,
        string? processName,
        bool allMatches,
        bool force,
        bool entireProcessTree,
        int timeoutMs,
        Guid commandId,
        CancellationToken cancellationToken);
}
