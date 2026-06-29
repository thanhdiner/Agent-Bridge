using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

internal static class ScreenInputDispatch
{
    public static Task<CommandResult<ScreenClickResult>> HandleClickAsync(
        IUiAutomationExecutor? executor,
        ScreenClickCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is null)
            return Task.FromResult(Unavailable<ScreenClickResult>(command.CommandId, "Screen clicking is not configured on this agent."));

        return executor.ClickScreenAsync(
            command.ExpectedForegroundWindowHandle,
            command.X,
            command.Y,
            command.MonitorIndex,
            command.Button,
            command.ClickCount,
            command.ExpectedProcessId,
            command.ExpectedWindowTitle,
            command.CommandId,
            cancellationToken);
    }

    public static Task<CommandResult<ScreenDragResult>> HandleDragAsync(
        IUiAutomationExecutor? executor,
        ScreenDragCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is null)
            return Task.FromResult(Unavailable<ScreenDragResult>(command.CommandId, "Screen dragging is not configured on this agent."));

        return executor.DragScreenAsync(
            command.ExpectedForegroundWindowHandle,
            command.StartX,
            command.StartY,
            command.EndX,
            command.EndY,
            command.StartMonitorIndex,
            command.EndMonitorIndex,
            command.Button,
            command.DurationMs,
            command.Steps,
            command.ExpectedProcessId,
            command.ExpectedWindowTitle,
            command.CommandId,
            cancellationToken);
    }

    public static Task<CommandResult<ScreenScrollResult>> HandleScrollAsync(
        IUiAutomationExecutor? executor,
        ScreenScrollCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is null)
            return Task.FromResult(Unavailable<ScreenScrollResult>(command.CommandId, "Screen scrolling is not configured on this agent."));

        return executor.ScrollScreenAsync(
            command.ExpectedForegroundWindowHandle,
            command.X,
            command.Y,
            command.MonitorIndex,
            command.Direction,
            command.Notches,
            command.ExpectedProcessId,
            command.ExpectedWindowTitle,
            command.CommandId,
            cancellationToken);
    }

    private static CommandResult<T> Unavailable<T>(Guid commandId, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(ErrorCodes.UiAutomationUnavailable, message)
    };
}
