using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
namespace LocalMcp.Agent.Windows.UiAutomation;
internal static class UiToggleDispatch
{
    public static Task<CommandResult<UiToggleResult>> HandleAsync(
        IUiAutomationExecutor? executor,
        UiToggleCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is UiAutomationExecutor concrete)
        {
            return concrete.ToggleAsync(
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
        return Task.FromResult(new CommandResult<UiToggleResult>
        {
            CommandId = command.CommandId,
            Success = false,
            Error = new CommandError(
                ErrorCodes.UiAutomationUnavailable,
                "UI toggle is not configured on this agent.")
        });
    }
}
