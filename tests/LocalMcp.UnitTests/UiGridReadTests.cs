using System.Reflection;
using System.Text.Json;
using Interop.UIAutomationClient;
using LocalMcp.Agent.Windows.Connection;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalMcp.UnitTests;

public sealed class UiGridReadTests
{
    [Fact]
    public async Task MissingSelector_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.GridReadAsync(
            "0x1234", null, null, null, 0,
            0, 10, 0, 10, 100, false,
            Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task RequestedWindowAboveMaxCells_ReturnsLimitError()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.GridReadAsync(
            "0x1234", null, null, "DataGrid", 0,
            0, 20, 0, 20, 100, false,
            Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.UiGridLimitExceeded, result.Error?.Code);
    }

    [Fact]
    public async Task NegativeRowStart_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.GridReadAsync(
            "0x1234", null, null, "DataGrid", 0,
            -1, 10, 0, 10, 100, false,
            Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public void UiFindPatternMetadata_ContainsGridPatterns()
    {
        var field = typeof(UiAutomationExecutor).GetField(
            "UiFindPatterns",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var patterns = Assert.IsType<(int PatternId, string Name)[]>(field.GetValue(null));

        Assert.Contains(patterns, item => item.PatternId == UIA_PatternIds.UIA_GridPatternId && item.Name == "grid");
        Assert.Contains(patterns, item => item.PatternId == UIA_PatternIds.UIA_TablePatternId && item.Name == "table");
        Assert.Contains(patterns, item => item.PatternId == UIA_PatternIds.UIA_GridItemPatternId && item.Name == "grid-item");
        Assert.Contains(patterns, item => item.PatternId == UIA_PatternIds.UIA_TableItemPatternId && item.Name == "table-item");
        Assert.Contains(patterns, item => item.PatternId == UIA_PatternIds.UIA_VirtualizedItemPatternId && item.Name == "virtualized-item");
    }

    [Fact]
    public void AgentDeserializer_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"UiGridReadCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"windowHandle\":\"0x1234\",\"automationId\":\"orders\",\"name\":\"Orders\",\"controlType\":\"DataGrid\",\"occurrenceIndex\":2,\"rowStart\":5,\"rowCount\":10,\"columnStart\":1,\"columnCount\":4,\"maxCells\":40,\"focusWindow\":true}}";

        var command = DeserializeExtended(nameof(UiGridReadCommand), json);
        var gridCommand = Assert.IsType<UiGridReadCommand>(command);
        Assert.Equal("orders", gridCommand.AutomationId);
        Assert.Equal("Orders", gridCommand.Name);
        Assert.Equal("DataGrid", gridCommand.ControlType);
        Assert.Equal(2, gridCommand.OccurrenceIndex);
        Assert.Equal(5, gridCommand.RowStart);
        Assert.Equal(10, gridCommand.RowCount);
        Assert.Equal(1, gridCommand.ColumnStart);
        Assert.Equal(4, gridCommand.ColumnCount);
        Assert.Equal(40, gridCommand.MaxCells);
        Assert.True(gridCommand.FocusWindow);
    }

    [Fact]
    public void Command_RoundTripsWithDefaults()
    {
        var command = new UiGridReadCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = "0x1234",
            ControlType = "Table"
        };

        var json = JsonSerializer.Serialize(command, JsonOptions.Default);
        var roundTrip = JsonSerializer.Deserialize<UiGridReadCommand>(json, JsonOptions.Default);

        Assert.NotNull(roundTrip);
        Assert.Equal(0, roundTrip.RowStart);
        Assert.Equal(50, roundTrip.RowCount);
        Assert.Equal(0, roundTrip.ColumnStart);
        Assert.Equal(20, roundTrip.ColumnCount);
        Assert.Equal(1000, roundTrip.MaxCells);
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
