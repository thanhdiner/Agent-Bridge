using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public interface IUiAutomationExecutor
{
    Task<CommandResult<WindowListResult>> ListWindowsAsync(
        bool includeInvisible,
        bool includeUntitled,
        int maxWindows,
        Guid commandId,
        CancellationToken cancellationToken);

    Task<CommandResult<UiTreeResult>> GetTreeAsync(
        string windowHandle,
        int maxDepth,
        int maxNodes,
        Guid commandId,
        CancellationToken cancellationToken);
}
