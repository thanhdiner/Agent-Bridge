using System.Reflection;
using System.Text.Json;
using LocalMcp.Agent.Windows.Connection;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using Microsoft.Extensions.Logging.Abstractions;
namespace LocalMcp.UnitTests;
public sealed class UiRangeValueTests
{
    [Theory]
    [InlineData("GET", UiRangeValueActions.Get)]
    [InlineData(" set ", UiRangeValueActions.Set)]
    [InlineData("Increase", UiRangeValueActions.Increase)]
    [InlineData("decrease", UiRangeValueActions.Decrease)]
    public void Action_Normalizes(string input, string expected)
    {
        Assert.True(UiRangeValueActions.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }
    [Fact]
    public async Task InvalidAction_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.RangeValueAsync(
            "0x1234",
            "raise",
            null,
            null,
            "Volume",
            "Slider",
            0,
            true,
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }
    [Fact]
    public async Task SetWithoutValue_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.RangeValueAsync(
            "0x1234",
            UiRangeValueActions.Set,
            null,
            null,
            "Volume",
            "Slider",
            0,
            true,
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }
    [Fact]
    public async Task GetWithValue_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.RangeValueAsync(
            "0x1234",
            UiRangeValueActions.Get,
            50,
            null,
            "Volume",
            "Slider",
            0,
            true,
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }
    [Theory]
    [InlineData(10d, 10d, true)]
    [InlineData(10d, 10.000000001d, true)]
    [InlineData(10d, 10.01d, false)]
    public void RangeValueEquivalence_UsesNumericTolerance(double actual, double expected, bool equivalent)
    {
        Assert.Equal(equivalent, UiAutomationExecutor.AreRangeValuesEquivalent(actual, expected));
    }
    [Fact]
    public void AgentDeserializer_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"UiRangeValueCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"windowHandle\":\"0x1234\",\"action\":\"set\",\"value\":42.5,\"automationId\":\"volume\",\"name\":\"Volume\",\"controlType\":\"Slider\",\"occurrenceIndex\":2,\"focusWindow\":false}}";
        var command = DeserializeExtended(nameof(UiRangeValueCommand), json);
        var rangeCommand = Assert.IsType<UiRangeValueCommand>(command);
        Assert.Equal(UiRangeValueActions.Set, rangeCommand.Action);
        Assert.Equal(42.5, rangeCommand.Value);
        Assert.Equal("volume", rangeCommand.AutomationId);
        Assert.Equal("Volume", rangeCommand.Name);
        Assert.Equal("Slider", rangeCommand.ControlType);
        Assert.Equal(2, rangeCommand.OccurrenceIndex);
        Assert.False(rangeCommand.FocusWindow);
    }
    [Fact]
    public void AgentDeserializer_WithoutOptionalFields_UsesDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"UiRangeValueCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"windowHandle\":\"0x1234\",\"name\":\"Volume\"}}";
        var command = DeserializeExtended(nameof(UiRangeValueCommand), json);
        var rangeCommand = Assert.IsType<UiRangeValueCommand>(command);
        Assert.Equal(UiRangeValueActions.Get, rangeCommand.Action);
        Assert.Null(rangeCommand.Value);
        Assert.Equal(0, rangeCommand.OccurrenceIndex);
        Assert.True(rangeCommand.FocusWindow);
    }
    [Fact]
    public void Command_RoundTripsThroughJsonOptions()
    {
        var command = new UiRangeValueCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = "0x1234",
            Action = UiRangeValueActions.Set,
            Value = 37.25,
            Name = "Volume",
            ControlType = "Slider",
            OccurrenceIndex = 1,
            FocusWindow = false
        };
        var json = JsonSerializer.Serialize(command, JsonOptions.Default);
        var roundTrip = JsonSerializer.Deserialize<UiRangeValueCommand>(json, JsonOptions.Default);
        Assert.NotNull(roundTrip);
        Assert.Equal(UiRangeValueActions.Set, roundTrip.Action);
        Assert.Equal(37.25, roundTrip.Value);
        Assert.Equal("Volume", roundTrip.Name);
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
