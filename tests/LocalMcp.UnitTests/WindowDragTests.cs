using System.Reflection;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Gateway.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class WindowDragTests
{
    [Fact]
    public void Tool_HasExpectedMetadataAndSchema()
    {
        var method = typeof(WindowCoordinateTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "window_drag");
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attribute);
        Assert.False(attribute!.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.False(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);

        var parameters = method.GetParameters().Select(parameter => parameter.Name!).ToHashSet();
        Assert.True(new[]
        {
            "deviceId", "windowHandle", "startX", "startY", "endX", "endY", "button",
            "durationMs", "steps", "expectedProcessId", "expectedWindowTitle"
        }.ToHashSet().SetEquals(parameters));
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, -100001, 0)]
    [InlineData(0, 0, 0, 100001)]
    public async Task DragWindowAsync_InvalidCoordinates_ReturnsInvalidRequest(
        int startX, int startY, int endX, int endY)
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.DragWindowAsync(
            "0x1234", startX, startY, endX, endY, "left", 300, 20,
            null, null, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task DragWindowAsync_NegativeEndCoordinates_PassValidation()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.DragWindowAsync(
            "0x1234", 10, 10, -10, -20, "left", 300, 20,
            null, null, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WindowNotFound, result.Error?.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public async Task DragWindowAsync_InvalidDuration_ReturnsInvalidRequest(int durationMs)
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.DragWindowAsync(
            "0x1234", 0, 0, 10, 10, "left", durationMs, 20,
            null, null, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(241)]
    public async Task DragWindowAsync_InvalidSteps_ReturnsInvalidRequest(int steps)
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.DragWindowAsync(
            "0x1234", 0, 0, 10, 10, "left", 300, steps,
            null, null, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task DragWindowAsync_InvalidButton_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.DragWindowAsync(
            "0x1234", 0, 0, 10, 10, "side", 300, 20,
            null, null, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public void TryTranslateWindowDragEndpoint_UsesInitialWindowOrigin()
    {
        Assert.True(UiAutomationExecutor.TryTranslateWindowDragEndpoint(
            -8, -8, 2000, 1100, out var screenX, out var screenY));
        Assert.Equal(1992, screenX);
        Assert.Equal(1092, screenY);
    }

    [Fact]
    public void InterpolateWindowDragCoordinate_MapsMidpointAndEndpoint()
    {
        Assert.Equal(50, UiAutomationExecutor.InterpolateWindowDragCoordinate(0, 100, 5, 10));
        Assert.Equal(100, UiAutomationExecutor.InterpolateWindowDragCoordinate(0, 100, 10, 10));
        Assert.Equal(-50, UiAutomationExecutor.InterpolateWindowDragCoordinate(0, -100, 5, 10));
    }
}
