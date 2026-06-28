using System.Text.Json;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;

namespace LocalMcp.Agent.Windows.Connection;

public sealed partial class GatewayConnection
{
    private static AgentCommand? DeserializeExtendedCommand(string? typeName, string rawJson) =>
        typeName switch
        {
            nameof(UiTreeCommand) => JsonSerializer.Deserialize<UiTreeCommand>(rawJson, JsonOptions.Default),
            _ => null
        };
}
