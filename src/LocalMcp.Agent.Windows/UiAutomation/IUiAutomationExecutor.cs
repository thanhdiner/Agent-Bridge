using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public interface IUiAutomationExecutor
{
    Task<CommandResult<UiTreeResult>> GetTreeAsync(
        string windowHandle,
        int maxDepth,
        int maxNodes,
        Guid commandId,
        CancellationToken cancellationToken);
}
