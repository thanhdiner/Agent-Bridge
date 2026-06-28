using System.Reflection;
using System.Text.Json;
using LocalMcp.Agent.Windows.Connection;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalMcp.UnitTests;

public sealed class UiExpandCollapseTests
{
    [Theory]
    [InlineData("EXPAND", UiExpandCollapseActions.Expand)]
    [InlineData(" collapse ", UiExpandCollapseActions.Collapse)]
    [InlineData("Toggle", UiExpandCollapseActions.Toggle)]
    public void Action_Normalizes(string input, string expected)
    {
        Assert.True(UiExpandCollapseActions.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task InvalidAction_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.ExpandCollapseAsync(
            "0x1234",
            "open",
            null,
            "Options",
            "ComboBox",
            0,
            true,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task MissingSelector_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.ExpandCollapseAsync(
            "0x1234",
            UiExpandCollapseActions.Toggle,
            null,
            null,
            "ComboBox",
            0,
            true,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public void AgentDeserializer_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"UiExpandCollapseCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"windowHandle\":\"0x1234\",\"action\":\"expand\",\"automationId\":\"combo-1\",\"name\":\"Options\",\"controlType\":\"ComboBox\",\"occurrenceIndex\":2,\"focusWindow\":false}}";

        var command = DeserializeExtended(nameof(UiExpandCollapseCommand), json);

        var expandCommand = Assert.IsType<UiExpandCollapseCommand>(command);
        Assert.Equal(UiExpandCollapseActions.Expand, expandCommand.Action);
        Assert.Equal("combo-1", expandCommand.AutomationId);
        Assert.Equal("Options", expandCommand.Name);
        Assert.Equal("ComboBox", expandCommand.ControlType);
        Assert.Equal(2, expandCommand.OccurrenceIndex);
        Assert.False(expandCommand.FocusWindow);
    }

    [Fact]
    public void AgentDeserializer_WithoutOptionalFields_UsesDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"UiExpandCollapseCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"windowHandle\":\"0x1234\",\"name\":\"Options\"}}";

        var command = DeserializeExtended(nameof(UiExpandCollapseCommand), json);

        var expandCommand = Assert.IsType<UiExpandCollapseCommand>(command);
        Assert.Equal(UiExpandCollapseActions.Toggle, expandCommand.Action);
        Assert.Equal(0, expandCommand.OccurrenceIndex);
        Assert.True(expandCommand.FocusWindow);
    }

    [Fact]
    public void Command_RoundTripsThroughJsonOptions()
    {
        var command = new UiExpandCollapseCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = "0x1234",
            Action = UiExpandCollapseActions.Collapse,
            Name = "Options",
            ControlType = "ComboBox",
            OccurrenceIndex = 1,
            FocusWindow = false
        };

        var json = JsonSerializer.Serialize(command, JsonOptions.Default);
        var roundTrip = JsonSerializer.Deserialize<UiExpandCollapseCommand>(json, JsonOptions.Default);

        Assert.NotNull(roundTrip);
        Assert.Equal(UiExpandCollapseActions.Collapse, roundTrip.Action);
        Assert.Equal("Options", roundTrip.Name);
        Assert.Equal(1, roundTrip.OccurrenceIndex);
        Assert.False(roundTrip.FocusWindow);
    }

    private static AgentCommand? DeserializeExtended(string commandType, string json)
    {
        var method = typeof(GatewayConnection).GetMethod(
            "DeserializeExtendedCommand",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<AgentCommand?>(method.Invoke(null, [commandType, json]));
    }
}
