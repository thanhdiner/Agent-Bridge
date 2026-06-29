using System.Text.Json;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;

namespace LocalMcp.Agent.Windows.Connection;

public sealed partial class GatewayConnection
{
    private static AgentCommand? DeserializeExtendedCommand(string? typeName, string rawJson) =>
        typeName switch
        {
            nameof(AppResolveCommand) => JsonSerializer.Deserialize<AppResolveCommand>(rawJson, JsonOptions.Default),
            nameof(AppOpenCommand) => JsonSerializer.Deserialize<AppOpenCommand>(rawJson, JsonOptions.Default),
            nameof(AppCloseCommand) => JsonSerializer.Deserialize<AppCloseCommand>(rawJson, JsonOptions.Default),
            nameof(ProcessWaitCommand) => JsonSerializer.Deserialize<ProcessWaitCommand>(rawJson, JsonOptions.Default),
            nameof(AppLaunchCommand) => JsonSerializer.Deserialize<AppLaunchCommand>(rawJson, JsonOptions.Default),
            nameof(WindowListCommand) => JsonSerializer.Deserialize<WindowListCommand>(rawJson, JsonOptions.Default),
            nameof(WindowWaitCommand) => JsonSerializer.Deserialize<WindowWaitCommand>(rawJson, JsonOptions.Default),
            nameof(WindowFocusCommand) => JsonSerializer.Deserialize<WindowFocusCommand>(rawJson, JsonOptions.Default),
            nameof(WindowCloseCommand) => JsonSerializer.Deserialize<WindowCloseCommand>(rawJson, JsonOptions.Default),
            nameof(WindowMoveCommand) => JsonSerializer.Deserialize<WindowMoveCommand>(rawJson, JsonOptions.Default),
            nameof(WindowScreenshotCommand) => JsonSerializer.Deserialize<WindowScreenshotCommand>(rawJson, JsonOptions.Default),
            nameof(ScreenScreenshotCommand) => JsonSerializer.Deserialize<ScreenScreenshotCommand>(rawJson, JsonOptions.Default),
            nameof(ScreenClickCommand) => JsonSerializer.Deserialize<ScreenClickCommand>(rawJson, JsonOptions.Default),
            nameof(ScreenDragCommand) => JsonSerializer.Deserialize<ScreenDragCommand>(rawJson, JsonOptions.Default),
            nameof(ScreenScrollCommand) => JsonSerializer.Deserialize<ScreenScrollCommand>(rawJson, JsonOptions.Default),
            nameof(WindowClickCommand) => JsonSerializer.Deserialize<WindowClickCommand>(rawJson, JsonOptions.Default),
            nameof(WindowDragCommand) => JsonSerializer.Deserialize<WindowDragCommand>(rawJson, JsonOptions.Default),
            nameof(UiClickCommand) => JsonSerializer.Deserialize<UiClickCommand>(rawJson, JsonOptions.Default),
            nameof(UiSelectCommand) => JsonSerializer.Deserialize<UiSelectCommand>(rawJson, JsonOptions.Default),
            nameof(UiExpandCollapseCommand) => JsonSerializer.Deserialize<UiExpandCollapseCommand>(rawJson, JsonOptions.Default),
            nameof(UiToggleCommand) => JsonSerializer.Deserialize<UiToggleCommand>(rawJson, JsonOptions.Default),
            nameof(UiRangeValueCommand) => JsonSerializer.Deserialize<UiRangeValueCommand>(rawJson, JsonOptions.Default),
            nameof(UiGridReadCommand) => JsonSerializer.Deserialize<UiGridReadCommand>(rawJson, JsonOptions.Default),
            nameof(UiGridSelectCommand) => JsonSerializer.Deserialize<UiGridSelectCommand>(rawJson, JsonOptions.Default),
            nameof(UiTextReadCommand) => JsonSerializer.Deserialize<UiTextReadCommand>(rawJson, JsonOptions.Default),
            nameof(UiGetStateCommand) => JsonSerializer.Deserialize<UiGetStateCommand>(rawJson, JsonOptions.Default),
            nameof(UiFocusCommand) => JsonSerializer.Deserialize<UiFocusCommand>(rawJson, JsonOptions.Default),
            nameof(UiGetValueCommand) => JsonSerializer.Deserialize<UiGetValueCommand>(rawJson, JsonOptions.Default),
            nameof(UiSetValueCommand) => JsonSerializer.Deserialize<UiSetValueCommand>(rawJson, JsonOptions.Default),
            nameof(UiPressKeyCommand) => JsonSerializer.Deserialize<UiPressKeyCommand>(rawJson, JsonOptions.Default),
            nameof(UiTypeTextCommand) => JsonSerializer.Deserialize<UiTypeTextCommand>(rawJson, JsonOptions.Default),
            nameof(UiScrollCommand) => JsonSerializer.Deserialize<UiScrollCommand>(rawJson, JsonOptions.Default),
            nameof(UiWaitCommand) => JsonSerializer.Deserialize<UiWaitCommand>(rawJson, JsonOptions.Default),
            nameof(UiFindCommand) => JsonSerializer.Deserialize<UiFindCommand>(rawJson, JsonOptions.Default),
            nameof(UiTreeCommand) => JsonSerializer.Deserialize<UiTreeCommand>(rawJson, JsonOptions.Default),
            _ => null
        };
}
