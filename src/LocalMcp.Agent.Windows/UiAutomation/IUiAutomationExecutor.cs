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

    Task<CommandResult<WindowWaitResult>> WaitForWindowAsync(
        string? windowHandle,
        int? processId,
        string? processName,
        string? className,
        string? title,
        string? titleContains,
        int occurrenceIndex,
        string condition,
        string? expectedTitle,
        bool includeInvisible,
        int timeoutMs,
        int pollIntervalMs,
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

    Task<CommandResult<UiGetValueResult>> GetValueAsync(
        string windowHandle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken);

    Task<CommandResult<UiSetValueResult>> SetValueAsync(
        string windowHandle,
        string value,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        bool append,
        Guid commandId,
        CancellationToken cancellationToken);

    Task<CommandResult<UiWaitResult>> WaitAsync(
        string windowHandle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        string condition,
        string? expectedValue,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken);

    Task<CommandResult<UiFindResult>> FindAsync(
        string windowHandle,
        string? automationId,
        string? nameContains,
        string? controlType,
        int maxDepth,
        int maxResults,
        Guid commandId,
        CancellationToken cancellationToken);

    Task<CommandResult<UiTreeResult>> GetTreeAsync(
        string windowHandle,
        int maxDepth,
        int maxNodes,
        Guid commandId,
        CancellationToken cancellationToken);
}
