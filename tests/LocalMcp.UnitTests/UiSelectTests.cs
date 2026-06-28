using System.Reflection;
using System.Text.Json;
using LocalMcp.Agent.Windows.Connection;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalMcp.UnitTests;

public sealed class UiSelectTests
{
    [Theory]
    [InlineData("SELECT", UiSelectActions.Select)]
    [InlineData(" add ", UiSelectActions.Add)]
    [InlineData("Remove", UiSelectActions.Remove)]
    public void Action_Normalizes(string input, string expected)
    {
        Assert.True(UiSelectActions.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(UiSelectActions.Select, true)]
    [InlineData(UiSelectActions.Add, true)]
    [InlineData(UiSelectActions.Remove, false)]
    public void Action_MapsExpectedSelectedState(string action, bool expected)
    {
        Assert.Equal(expected, UiSelectActions.ExpectedSelected(action));
    }

    [Fact]
    public async Task InvalidAction_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.SelectAsync(
            "0x1234",
            "toggle",
            null,
            "Second",
            "ListItem",
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

        var result = await executor.SelectAsync(
            "0x1234",
            UiSelectActions.Select,
            null,
            null,
            "ListItem",
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
        var json = $"{{\"commandType\":\"UiSelectCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"windowHandle\":\"0x1234\",\"action\":\"add\",\"automationId\":\"item-2\",\"name\":\"Second\",\"controlType\":\"ListItem\",\"occurrenceIndex\":2,\"focusWindow\":false}}";

        var command = DeserializeExtended(nameof(UiSelectCommand), json);

        var selectCommand = Assert.IsType<UiSelectCommand>(command);
        Assert.Equal(UiSelectActions.Add, selectCommand.Action);
        Assert.Equal("item-2", selectCommand.AutomationId);
        Assert.Equal("Second", selectCommand.Name);
        Assert.Equal("ListItem", selectCommand.ControlType);
        Assert.Equal(2, selectCommand.OccurrenceIndex);
        Assert.False(selectCommand.FocusWindow);
    }

    [Fact]
    public void AgentDeserializer_WithoutOptionalFields_UsesDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"UiSelectCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"windowHandle\":\"0x1234\",\"name\":\"Second\"}}";

        var command = DeserializeExtended(nameof(UiSelectCommand), json);

        var selectCommand = Assert.IsType<UiSelectCommand>(command);
        Assert.Equal(UiSelectActions.Select, selectCommand.Action);
        Assert.Equal(0, selectCommand.OccurrenceIndex);
        Assert.True(selectCommand.FocusWindow);
    }

    [Fact]
    public void Command_RoundTripsThroughJsonOptions()
    {
        var command = new UiSelectCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = "0x1234",
            Action = UiSelectActions.Remove,
            Name = "Second",
            ControlType = "ListItem",
            OccurrenceIndex = 1,
            FocusWindow = false
        };

        var json = JsonSerializer.Serialize(command, JsonOptions.Default);
        var roundTrip = JsonSerializer.Deserialize<UiSelectCommand>(json, JsonOptions.Default);

        Assert.NotNull(roundTrip);
        Assert.Equal(UiSelectActions.Remove, roundTrip.Action);
        Assert.Equal("Second", roundTrip.Name);
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
