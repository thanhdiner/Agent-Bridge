using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

internal static class PhaseFourDispatch
{
    public static Task<CommandResult<ClipboardGetResult>> ClipboardGetAsync(
        IUiAutomationExecutor? executor,
        ClipboardGetCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is UiAutomationExecutor concrete)
            return concrete.ClipboardGetAsync(command.MaxCharacters, command.CommandId, cancellationToken);

        return Task.FromResult(Unavailable<ClipboardGetResult>(command.CommandId, "Clipboard access"));
    }

    public static Task<CommandResult<ClipboardSetResult>> ClipboardSetAsync(
        IUiAutomationExecutor? executor,
        ClipboardSetCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is UiAutomationExecutor concrete)
            return concrete.ClipboardSetAsync(command.Text, command.Verify, command.CommandId, cancellationToken);

        return Task.FromResult(Unavailable<ClipboardSetResult>(command.CommandId, "Clipboard access"));
    }

    public static Task<CommandResult<FileDialogSetPathResult>> FileDialogSetPathAsync(
        IUiAutomationExecutor? executor,
        FileDialogSetPathCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is UiAutomationExecutor concrete)
        {
            return concrete.FileDialogSetPathAsync(
                command.WindowHandle,
                command.Path,
                command.AutomationId,
                command.Name,
                command.ControlType,
                command.OccurrenceIndex,
                command.FocusWindow,
                command.Submit,
                command.CommandId,
                cancellationToken);
        }

        return Task.FromResult(Unavailable<FileDialogSetPathResult>(command.CommandId, "File dialog automation"));
    }

    private static CommandResult<T> Unavailable<T>(Guid commandId, string capability) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(
            ErrorCodes.UiAutomationUnavailable,
            $"{capability} is not configured on this agent.")
    };
}
