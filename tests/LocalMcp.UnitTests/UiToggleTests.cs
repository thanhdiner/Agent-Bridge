using System.Reflection;
using System.Text.Json;
using LocalMcp.Agent.Windows.Connection;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using Microsoft.Extensions.Logging.Abstractions;
namespace LocalMcp.UnitTests;
public sealed class UiToggleTests
{
    [Theory]
    [InlineData("ON", UiToggleActions.On)]
    [InlineData(" off ", UiToggleActions.Off)]
    [InlineData("Toggle", UiToggleActions.Toggle)]
    public void Action_Normalizes(string input, string expected)
    {
        Assert.True(UiToggleActions.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }
    [Fact]
    public async Task InvalidAction_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.ToggleAsync(
            "0x1234",
            "enable",
            null,
            "Remember me",
            "CheckBox",
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
        var result = await executor.ToggleAsync(
            "0x1234",
            UiToggleActions.Toggle,
            null,
            null,
            "CheckBox",
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
        var json = $"{{\"commandType\":\"UiToggleCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"windowHandle\":\"0x1234\",\"action\":\"on\",\"automationId\":\"check-1\",\"name\":\"Remember me\",\"controlType\":\"CheckBox\",\"occurrenceIndex\":2,\"focusWindow\":false}}";
        var command = DeserializeExtended(nameof(UiToggleCommand), json);
        var toggleCommand = Assert.IsType<UiToggleCommand>(command);
        Assert.Equal(UiToggleActions.On, toggleCommand.Action);
        Assert.Equal("check-1", toggleCommand.AutomationId);
        Assert.Equal("Remember me", toggleCommand.Name);
        Assert.Equal("CheckBox", toggleCommand.ControlType);
        Assert.Equal(2, toggleCommand.OccurrenceIndex);
        Assert.False(toggleCommand.FocusWindow);
    }
    [Fact]
    public void AgentDeserializer_WithoutOptionalFields_UsesDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"UiToggleCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"windowHandle\":\"0x1234\",\"name\":\"Remember me\"}}";
        var command = DeserializeExtended(nameof(UiToggleCommand), json);
        var toggleCommand = Assert.IsType<UiToggleCommand>(command);
        Assert.Equal(UiToggleActions.Toggle, toggleCommand.Action);
        Assert.Equal(0, toggleCommand.OccurrenceIndex);
        Assert.True(toggleCommand.FocusWindow);
    }
    [Fact]
    public void Command_RoundTripsThroughJsonOptions()
    {
        var command = new UiToggleCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = "0x1234",
            Action = UiToggleActions.Off,
            Name = "Remember me",
            ControlType = "CheckBox",
            OccurrenceIndex = 1,
            FocusWindow = false
        };
        var json = JsonSerializer.Serialize(command, JsonOptions.Default);
        var roundTrip = JsonSerializer.Deserialize<UiToggleCommand>(json, JsonOptions.Default);
        Assert.NotNull(roundTrip);
        Assert.Equal(UiToggleActions.Off, roundTrip.Action);
        Assert.Equal("Remember me", roundTrip.Name);
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
