using System.Text.Json;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.Commands;

public sealed partial class CommandHandler
{
    private async Task<CommandResult<JsonElement>> HandleWindowFocusAsync(
        WindowFocusCommand command,
        CancellationToken cancellationToken)
    {
        if (_uiAutomationExecutor is null)
            return WindowActionUnavailable(command.CommandId, "Window focus is not configured on this agent.");

        var result = await _uiAutomationExecutor.FocusWindowAsync(
            command.WindowHandle,
            command.CommandId,
            cancellationToken);
        return WindowActionToJson(result);
    }

    private async Task<CommandResult<JsonElement>> HandleWindowCloseAsync(
        WindowCloseCommand command,
        CancellationToken cancellationToken)
    {
        if (_uiAutomationExecutor is null)
            return WindowActionUnavailable(command.CommandId, "Window close is not configured on this agent.");

        var result = await _uiAutomationExecutor.CloseWindowAsync(
            command.WindowHandle,
            command.CommandId,
            cancellationToken);
        return WindowActionToJson(result);
    }

    private async Task<CommandResult<JsonElement>> HandleWindowMoveAsync(
        WindowMoveCommand command,
        CancellationToken cancellationToken)
    {
        if (_uiAutomationExecutor is null)
            return WindowActionUnavailable(command.CommandId, "Window movement is not configured on this agent.");

        var result = await _uiAutomationExecutor.MoveWindowAsync(
            command.WindowHandle,
            command.X,
            command.Y,
            command.Width,
            command.Height,
            command.RestoreIfNeeded,
            command.CommandId,
            cancellationToken);
        return WindowActionToJson(result);
    }

    private async Task<CommandResult<JsonElement>> HandleUiClickAsync(
        UiClickCommand command,
        CancellationToken cancellationToken)
    {
        if (_uiAutomationExecutor is null)
            return WindowActionUnavailable(command.CommandId, "UI interaction is not configured on this agent.");

        var result = await _uiAutomationExecutor.ClickAsync(
            command.WindowHandle,
            command.AutomationId,
            command.Name,
            command.ControlType,
            command.OccurrenceIndex,
            command.FocusWindow,
            command.CommandId,
            cancellationToken);
        return WindowActionToJson(result);
    }

    private async Task<CommandResult<JsonElement>> HandleUiGetValueAsync(
        UiGetValueCommand command,
        CancellationToken cancellationToken)
    {
        if (_uiAutomationExecutor is null)
            return WindowActionUnavailable(command.CommandId, "UI value reading is not configured on this agent.");

        var result = await _uiAutomationExecutor.GetValueAsync(
            command.WindowHandle,
            command.AutomationId,
            command.Name,
            command.ControlType,
            command.OccurrenceIndex,
            command.FocusWindow,
            command.CommandId,
            cancellationToken);
        return WindowActionToJson(result);
    }

    private async Task<CommandResult<JsonElement>> HandleUiSetValueAsync(
        UiSetValueCommand command,
        CancellationToken cancellationToken)
    {
        if (_uiAutomationExecutor is null)
            return WindowActionUnavailable(command.CommandId, "UI value writing is not configured on this agent.");

        var result = await _uiAutomationExecutor.SetValueAsync(
            command.WindowHandle,
            command.Value,
            command.AutomationId,
            command.Name,
            command.ControlType,
            command.OccurrenceIndex,
            command.FocusWindow,
            command.Append,
            command.CommandId,
            cancellationToken);
        return WindowActionToJson(result);
    }

    private async Task<CommandResult<JsonElement>> HandleUiPressKeyAsync(
        UiPressKeyCommand command,
        CancellationToken cancellationToken)
    {
        if (_uiAutomationExecutor is null)
            return WindowActionUnavailable(command.CommandId, "Keyboard input is not configured on this agent.");

        var result = await _uiAutomationExecutor.PressKeyAsync(
            command.WindowHandle,
            command.Keys,
            command.AutomationId,
            command.Name,
            command.ControlType,
            command.OccurrenceIndex,
            command.FocusWindow,
            command.CommandId,
            cancellationToken);
        return WindowActionToJson(result);
    }

    private async Task<CommandResult<JsonElement>> HandleUiTypeTextAsync(
        UiTypeTextCommand command,
        CancellationToken cancellationToken)
    {
        if (_uiAutomationExecutor is null)
            return WindowActionUnavailable(command.CommandId, "Text input is not configured on this agent.");

        var result = await _uiAutomationExecutor.TypeTextAsync(
            command.WindowHandle,
            command.Text,
            command.AutomationId,
            command.Name,
            command.ControlType,
            command.OccurrenceIndex,
            command.FocusWindow,
            command.CommandId,
            cancellationToken);
        return WindowActionToJson(result);
    }

    private async Task<CommandResult<JsonElement>> HandleUiWaitAsync(
        UiWaitCommand command,
        CancellationToken cancellationToken)
    {
        if (_uiAutomationExecutor is null)
            return WindowActionUnavailable(command.CommandId, "UI waiting is not configured on this agent.");

        var result = await _uiAutomationExecutor.WaitAsync(
            command.WindowHandle,
            command.AutomationId,
            command.Name,
            command.ControlType,
            command.OccurrenceIndex,
            command.Condition,
            command.ExpectedValue,
            command.TimeoutMs,
            command.PollIntervalMs,
            command.CommandId,
            cancellationToken);
        return WindowActionToJson(result);
    }

    private static CommandResult<JsonElement> WindowActionToJson<T>(CommandResult<T> result)
    {
        if (!result.Success || result.Data is null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = result.CommandId,
                Success = false,
                Error = result.Error
            };
        }

        return new CommandResult<JsonElement>
        {
            CommandId = result.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(
                result.Data,
                LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default)
        };
    }

    private static CommandResult<JsonElement> WindowActionUnavailable(Guid commandId, string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(ErrorCodes.UiAutomationUnavailable, message)
        };
}
