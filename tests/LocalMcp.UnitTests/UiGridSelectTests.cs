using System.Reflection;
using System.Text.Json;
using LocalMcp.Agent.Windows.Connection;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalMcp.UnitTests;

public sealed class UiGridSelectTests
{
    [Theory]
    [InlineData("SELECT", UiGridSelectActions.Select)]
    [InlineData(" add ", UiGridSelectActions.Add)]
    [InlineData("Remove", UiGridSelectActions.Remove)]
    [InlineData("activate", UiGridSelectActions.Activate)]
    public void Action_Normalizes(string input, string expected)
    {
        Assert.True(UiGridSelectActions.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(UiGridSelectActions.Select, true)]
    [InlineData(UiGridSelectActions.Add, true)]
    [InlineData(UiGridSelectActions.Remove, false)]
    public void ExpectedSelected_ReturnsState(string action, bool expected)
    {
        Assert.Equal(expected, UiGridSelectActions.ExpectedSelected(action));
    }

    [Fact]
    public async Task InvalidAction_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.GridSelectAsync(
            "0x1234", "open", null, null, "DataGrid", 0,
            0, 0, true, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task MissingSelector_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.GridSelectAsync(
            "0x1234", UiGridSelectActions.Select, null, null, null, 0,
            0, 0, true, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public async Task NegativeCoordinate_ReturnsInvalidRequest(int row, int column)
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.GridSelectAsync(
            "0x1234", UiGridSelectActions.Select, null, null, "DataGrid", 0,
            row, column, true, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public void AgentDeserializer_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"UiGridSelectCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"windowHandle\":\"0x1234\",\"action\":\"add\",\"automationId\":\"orders\",\"name\":\"Orders\",\"controlType\":\"DataGrid\",\"occurrenceIndex\":2,\"row\":5,\"column\":3,\"focusWindow\":false}}";

        var command = DeserializeExtended(nameof(UiGridSelectCommand), json);
        var gridCommand = Assert.IsType<UiGridSelectCommand>(command);
        Assert.Equal(UiGridSelectActions.Add, gridCommand.Action);
        Assert.Equal("orders", gridCommand.AutomationId);
        Assert.Equal("Orders", gridCommand.Name);
        Assert.Equal("DataGrid", gridCommand.ControlType);
        Assert.Equal(2, gridCommand.OccurrenceIndex);
        Assert.Equal(5, gridCommand.Row);
        Assert.Equal(3, gridCommand.Column);
        Assert.False(gridCommand.FocusWindow);
    }

    [Fact]
    public void Command_RoundTripsWithDefaults()
    {
        var command = new UiGridSelectCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = "0x1234",
            ControlType = "Table"
        };

        var json = JsonSerializer.Serialize(command, JsonOptions.Default);
        var roundTrip = JsonSerializer.Deserialize<UiGridSelectCommand>(json, JsonOptions.Default);

        Assert.NotNull(roundTrip);
        Assert.Equal(UiGridSelectActions.Select, roundTrip.Action);
        Assert.Equal(0, roundTrip.Row);
        Assert.Equal(0, roundTrip.Column);
        Assert.True(roundTrip.FocusWindow);
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
