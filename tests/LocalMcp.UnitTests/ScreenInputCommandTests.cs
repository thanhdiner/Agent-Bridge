using System.Text.Json;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;

namespace LocalMcp.UnitTests;

public sealed class ScreenInputCommandTests
{
    [Fact]
    public void DeserializeScreenClick_PreservesGuardsAndCoordinates()
    {
        const string json = "{\"commandId\":\"11111111-1111-1111-1111-111111111111\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"expectedForegroundWindowHandle\":\"0x1234\",\"x\":-40,\"y\":25,\"monitorIndex\":1,\"button\":\"right\",\"clickCount\":2,\"expectedProcessId\":77,\"expectedWindowTitle\":\"Target\"}";

        var command = JsonSerializer.Deserialize<ScreenClickCommand>(json, JsonOptions.Default);

        Assert.NotNull(command);
        Assert.Equal("0x1234", command!.ExpectedForegroundWindowHandle);
        Assert.Equal(-40, command.X);
        Assert.Equal(25, command.Y);
        Assert.Equal(1, command.MonitorIndex);
        Assert.Equal("right", command.Button);
        Assert.Equal(2, command.ClickCount);
        Assert.Equal(77, command.ExpectedProcessId);
        Assert.Equal("Target", command.ExpectedWindowTitle);
    }

    [Fact]
    public void DeserializeScreenDrag_AppliesDefaults()
    {
        const string json = "{\"commandId\":\"22222222-2222-2222-2222-222222222222\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"expectedForegroundWindowHandle\":\"0x1234\",\"startX\":10,\"startY\":20,\"endX\":30,\"endY\":40}";

        var command = JsonSerializer.Deserialize<ScreenDragCommand>(json, JsonOptions.Default);

        Assert.NotNull(command);
        Assert.Equal(WindowMouseButtons.Left, command!.Button);
        Assert.Equal(300, command.DurationMs);
        Assert.Equal(20, command.Steps);
        Assert.Null(command.StartMonitorIndex);
        Assert.Null(command.EndMonitorIndex);
    }

    [Fact]
    public void DeserializeScreenScroll_PreservesDirectionAndNotches()
    {
        const string json = "{\"commandId\":\"33333333-3333-3333-3333-333333333333\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"expectedForegroundWindowHandle\":\"4660\",\"x\":100,\"y\":200,\"monitorIndex\":0,\"direction\":\"down\",\"notches\":4,\"expectedProcessId\":88}";

        var command = JsonSerializer.Deserialize<ScreenScrollCommand>(json, JsonOptions.Default);

        Assert.NotNull(command);
        Assert.Equal("4660", command!.ExpectedForegroundWindowHandle);
        Assert.Equal(100, command.X);
        Assert.Equal(200, command.Y);
        Assert.Equal(0, command.MonitorIndex);
        Assert.Equal("down", command.Direction);
        Assert.Equal(4, command.Notches);
        Assert.Equal(88, command.ExpectedProcessId);
    }
}
