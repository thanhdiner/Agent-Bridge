using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

internal static class WindowDragDispatch
{
    public static Task<CommandResult<WindowDragResult>> HandleAsync(IUiAutomationExecutor? executor, WindowDragCommand command, CancellationToken cancellationToken)
    {
        if (executor is null)
        {
            return Task.FromResult(new CommandResult<WindowDragResult>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(ErrorCodes.UiAutomationUnavailable, "Window drag is not configured on this agent.")
            });
        }

        return executor.DragWindowAsync(command.WindowHandle, command.StartX, command.StartY, command.EndX, command.EndY, command.Button, command.DurationMs, command.Steps, command.ExpectedProcessId, command.ExpectedWindowTitle, command.CommandId, cancellationToken);
    }
}
