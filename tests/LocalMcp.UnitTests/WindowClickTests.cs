using System.Reflection;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Gateway.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
namespace LocalMcp.UnitTests;
public sealed class WindowClickTests
{
    [Fact]
    public void Tool_HasExpectedMetadataAndSchema()
    {
        var method = typeof(WindowCoordinateTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "window_click");
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attribute);
        Assert.False(attribute!.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.False(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
        var parameters = method.GetParameters().Select(parameter => parameter.Name!).ToHashSet();
        Assert.True(new[]
        {
            "deviceId",
            "windowHandle",
            "x",
            "y",
            "button",
            "clickCount",
            "expectedProcessId",
            "expectedWindowTitle"
        }.ToHashSet().SetEquals(parameters));
    }
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(100001, 0)]
    [InlineData(0, 100001)]
    public async Task ClickWindowAsync_InvalidCoordinates_ReturnsInvalidRequest(int x, int y)
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.ClickWindowAsync(
            "0x1234",
            x,
            y,
            "left",
            1,
            null,
            null,
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task ClickWindowAsync_InvalidClickCount_ReturnsInvalidRequest(int clickCount)
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.ClickWindowAsync(
            "0x1234",
            0,
            0,
            "left",
            clickCount,
            null,
            null,
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }
    [Fact]
    public async Task ClickWindowAsync_InvalidButton_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.ClickWindowAsync(
            "0x1234",
            0,
            0,
            "side",
            1,
            null,
            null,
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }
    [Fact]
    public void TryTranslateWindowClickPoint_UsesWindowLocalCoordinates()
    {
        var translated = UiAutomationExecutor.TryTranslateWindowClickPoint(
            -8,
            -8,
            1936,
            1048,
            100,
            50,
            out var screenX,
            out var screenY);
        Assert.True(translated);
        Assert.Equal(92, screenX);
        Assert.Equal(42, screenY);
    }
    [Fact]
    public void TryTranslateWindowClickPoint_RejectsRightAndBottomEdges()
    {
        Assert.False(UiAutomationExecutor.TryTranslateWindowClickPoint(10, 20, 800, 600, 800, 10, out _, out _));
        Assert.False(UiAutomationExecutor.TryTranslateWindowClickPoint(10, 20, 800, 600, 10, 600, out _, out _));
    }
    [Fact]
    public void NormalizeWindowClickCoordinate_MapsVirtualDesktopEndpoints()
    {
        Assert.Equal(0, UiAutomationExecutor.NormalizeWindowClickCoordinate(-1920, -1920, 3840));
        Assert.Equal(65535, UiAutomationExecutor.NormalizeWindowClickCoordinate(1919, -1920, 3840));
    }
}
