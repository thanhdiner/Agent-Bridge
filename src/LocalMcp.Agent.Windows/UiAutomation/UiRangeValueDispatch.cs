using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
namespace LocalMcp.Agent.Windows.UiAutomation;
internal static class UiRangeValueDispatch
{
    public static Task<CommandResult<UiRangeValueResult>> HandleAsync(
        IUiAutomationExecutor? executor,
        UiRangeValueCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is UiAutomationExecutor concrete)
        {
            return concrete.RangeValueAsync(
                command.WindowHandle,
                command.Action,
                command.Value,
                command.AutomationId,
                command.Name,
                command.ControlType,
                command.OccurrenceIndex,
                command.FocusWindow,
                command.CommandId,
                cancellationToken);
        }
        return Task.FromResult(new CommandResult<UiRangeValueResult>
        {
            CommandId = command.CommandId,
            Success = false,
            Error = new CommandError(
                ErrorCodes.UiAutomationUnavailable,
                "UI range value automation is not configured on this agent.")
        });
    }
}
