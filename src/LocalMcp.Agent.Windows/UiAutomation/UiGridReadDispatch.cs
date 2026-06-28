using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

internal static class UiGridReadDispatch
{
    public static Task<CommandResult<UiGridReadResult>> HandleAsync(
        IUiAutomationExecutor? executor,
        UiGridReadCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is UiAutomationExecutor concrete)
        {
            return concrete.GridReadAsync(
                command.WindowHandle,
                command.AutomationId,
                command.Name,
                command.ControlType,
                command.OccurrenceIndex,
                command.RowStart,
                command.RowCount,
                command.ColumnStart,
                command.ColumnCount,
                command.MaxCells,
                command.FocusWindow,
                command.CommandId,
                cancellationToken);
        }

        return Task.FromResult(new CommandResult<UiGridReadResult>
        {
            CommandId = command.CommandId,
            Success = false,
            Error = new CommandError(
                ErrorCodes.UiAutomationUnavailable,
                "UI grid reading is not configured on this agent.")
        });
    }
}
