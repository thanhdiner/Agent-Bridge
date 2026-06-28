using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

internal static class UiExpandCollapseDispatch
{
    public static Task<CommandResult<UiExpandCollapseResult>> HandleAsync(
        IUiAutomationExecutor? executor,
        UiExpandCollapseCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is UiAutomationExecutor concrete)
        {
            return concrete.ExpandCollapseAsync(
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

        return Task.FromResult(new CommandResult<UiExpandCollapseResult>
        {
            CommandId = command.CommandId,
            Success = false,
            Error = new CommandError(
                ErrorCodes.UiAutomationUnavailable,
                "UI expand/collapse is not configured on this agent.")
        });
    }
}
