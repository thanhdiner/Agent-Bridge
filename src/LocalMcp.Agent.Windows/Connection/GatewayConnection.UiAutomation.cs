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
            nameof(AppLaunchCommand) => JsonSerializer.Deserialize<AppLaunchCommand>(rawJson, JsonOptions.Default),
            nameof(WindowListCommand) => JsonSerializer.Deserialize<WindowListCommand>(rawJson, JsonOptions.Default),
            nameof(WindowWaitCommand) => JsonSerializer.Deserialize<WindowWaitCommand>(rawJson, JsonOptions.Default),
            nameof(WindowFocusCommand) => JsonSerializer.Deserialize<WindowFocusCommand>(rawJson, JsonOptions.Default),
            nameof(WindowCloseCommand) => JsonSerializer.Deserialize<WindowCloseCommand>(rawJson, JsonOptions.Default),
            nameof(WindowMoveCommand) => JsonSerializer.Deserialize<WindowMoveCommand>(rawJson, JsonOptions.Default),
            nameof(UiClickCommand) => JsonSerializer.Deserialize<UiClickCommand>(rawJson, JsonOptions.Default),
            nameof(UiGetValueCommand) => JsonSerializer.Deserialize<UiGetValueCommand>(rawJson, JsonOptions.Default),
            nameof(UiSetValueCommand) => JsonSerializer.Deserialize<UiSetValueCommand>(rawJson, JsonOptions.Default),
            nameof(UiWaitCommand) => JsonSerializer.Deserialize<UiWaitCommand>(rawJson, JsonOptions.Default),
            nameof(UiTreeCommand) => JsonSerializer.Deserialize<UiTreeCommand>(rawJson, JsonOptions.Default),
            _ => null
        };
}
