using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

internal static class UiTextReadDispatch
{
    public static Task<CommandResult<UiTextReadResult>> HandleAsync(
        IUiAutomationExecutor? executor,
        UiTextReadCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is UiAutomationExecutor concrete)
        {
            return concrete.TextReadAsync(
                command.WindowHandle,
                command.Scope,
                command.AutomationId,
                command.Name,
                command.ControlType,
                command.OccurrenceIndex,
                command.StartLine,
                command.LineCount,
                command.MaxCharacters,
                command.FocusWindow,
                command.CommandId,
                cancellationToken);
        }

        return Task.FromResult(new CommandResult<UiTextReadResult>
        {
            CommandId = command.CommandId,
            Success = false,
            Error = new CommandError(
                ErrorCodes.UiAutomationUnavailable,
                "UI text reading is not configured on this agent.")
        });
    }
}
