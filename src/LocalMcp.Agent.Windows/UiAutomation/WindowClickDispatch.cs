using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

internal static class WindowClickDispatch
{
    public static Task<CommandResult<WindowClickResult>> HandleAsync(
        IUiAutomationExecutor? executor,
        WindowClickCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is null)
        {
            return Task.FromResult(new CommandResult<WindowClickResult>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(
                    ErrorCodes.UiAutomationUnavailable,
                    "Window coordinate clicking is not configured on this agent.")
            });
        }

        return executor.ClickWindowAsync(
            command.WindowHandle,
            command.X,
            command.Y,
            command.Button,
            command.ClickCount,
            command.ExpectedProcessId,
            command.ExpectedWindowTitle,
            command.CommandId,
            cancellationToken);
    }
}
