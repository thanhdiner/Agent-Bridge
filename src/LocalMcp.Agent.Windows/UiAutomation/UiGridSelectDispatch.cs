using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

internal static class UiGridSelectDispatch
{
    public static Task<CommandResult<UiGridSelectResult>> HandleAsync(
        IUiAutomationExecutor? executor,
        UiGridSelectCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is UiAutomationExecutor concrete)
        {
            return concrete.GridSelectAsync(
                command.WindowHandle,
                command.Action,
                command.AutomationId,
                command.Name,
                command.ControlType,
                command.OccurrenceIndex,
                command.Row,
                command.Column,
                command.FocusWindow,
                command.CommandId,
                cancellationToken);
        }

        return Task.FromResult(new CommandResult<UiGridSelectResult>
        {
            CommandId = command.CommandId,
            Success = false,
            Error = new CommandError(
                ErrorCodes.UiAutomationUnavailable,
                "UI grid selection is not configured on this agent.")
        });
    }
}
