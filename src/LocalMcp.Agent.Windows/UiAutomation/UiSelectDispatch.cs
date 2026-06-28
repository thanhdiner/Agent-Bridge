using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

internal static class UiSelectDispatch
{
    public static Task<CommandResult<UiSelectResult>> HandleAsync(
        IUiAutomationExecutor? executor,
        UiSelectCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is UiAutomationExecutor concrete)
        {
            return concrete.SelectAsync(
                command.WindowHandle,
                command.Action,
                command.AutomationId,
                command.Name,
                command.ControlType,
                command.OccurrenceIndex,
                command.FocusWindow,
                command.CommandId,
                cancellationToken);
        }

        return Task.FromResult(new CommandResult<UiSelectResult>
        {
            CommandId = command.CommandId,
            Success = false,
            Error = new CommandError(
                ErrorCodes.UiAutomationUnavailable,
                "UI selection is not configured on this agent.")
        });
    }
}
