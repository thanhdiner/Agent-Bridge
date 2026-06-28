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

    Task<CommandResult<WindowFocusResult>> FocusWindowAsync(
        string windowHandle,
        Guid commandId,
        CancellationToken cancellationToken);

    Task<CommandResult<WindowCloseResult>> CloseWindowAsync(
        string windowHandle,
        Guid commandId,
        CancellationToken cancellationToken);

    Task<CommandResult<WindowMoveResult>> MoveWindowAsync(
        string windowHandle,
        int x,
        int y,
        int width,
        int height,
        bool restoreIfNeeded,
        Guid commandId,
        CancellationToken cancellationToken);

    Task<CommandResult<UiClickResult>> ClickAsync(
        string windowHandle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken);

    Task<CommandResult<UiTreeResult>> GetTreeAsync(
        string windowHandle,
        int maxDepth,
        int maxNodes,
        Guid commandId,
        CancellationToken cancellationToken);
}
