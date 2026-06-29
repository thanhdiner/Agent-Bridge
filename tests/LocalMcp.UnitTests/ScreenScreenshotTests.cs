using System.Reflection;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Gateway.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class ScreenScreenshotTests
{
    [Fact]
    public void Tool_HasExpectedMetadataAndSchema()
    {
        var method = typeof(ScreenCaptureTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "screen_screenshot");
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(attribute);
        Assert.True(attribute!.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
        Assert.Equal(
            new[] { "deviceId", "monitorIndex", "x", "y", "width", "height", "maxWidth", "maxHeight" },
            method.GetParameters().Select(parameter => parameter.Name).ToArray());
    }

    [Fact]
    public async Task PartialRegion_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.CaptureScreenScreenshotAsync(
            null, 10, 20, 100, null, 4096, 4096, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task MonitorAndRegion_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.CaptureScreenScreenshotAsync(
            0, 10, 20, 100, 100, 4096, 4096, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task OverflowingRegion_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.CaptureScreenScreenshotAsync(
            null, int.MaxValue - 5, 0, 10, 10, 4096, 4096, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public void ResolveCaptureBounds_SupportsNegativeCoordinates()
    {
        IReadOnlyList<UiAutomationExecutor.ScreenMonitorSnapshot> monitors =
        [
            new(0, "PRIMARY", true,
                new UiAutomationExecutor.ScreenCaptureBounds(0, 0, 1920, 1080),
                new UiAutomationExecutor.ScreenCaptureBounds(0, 0, 1920, 1040), 144, 144),
            new(1, "LEFT", false,
                new UiAutomationExecutor.ScreenCaptureBounds(-1280, 0, 1280, 1024),
                new UiAutomationExecutor.ScreenCaptureBounds(-1280, 0, 1280, 984), 96, 96)
        ];
        var virtualBounds = new UiAutomationExecutor.ScreenCaptureBounds(-1280, 0, 3200, 1080);

        var success = UiAutomationExecutor.TryResolveCaptureBounds(
            monitors, virtualBounds, null, -1200, 50, 300, 200,
            out var bounds, out var mode, out var selectedMonitor, out var error);

        Assert.True(success, error);
        Assert.Equal(new UiAutomationExecutor.ScreenCaptureBounds(-1200, 50, 300, 200), bounds);
        Assert.Equal("region", mode);
        Assert.Equal(1, selectedMonitor);
    }

    [Fact]
    public void ResolveCaptureBounds_RejectsRegionOutsideVirtualDesktop()
    {
        IReadOnlyList<UiAutomationExecutor.ScreenMonitorSnapshot> monitors =
        [
            new(0, "PRIMARY", true,
                new UiAutomationExecutor.ScreenCaptureBounds(0, 0, 1920, 1080),
                new UiAutomationExecutor.ScreenCaptureBounds(0, 0, 1920, 1040), 144, 144)
        ];
        var virtualBounds = new UiAutomationExecutor.ScreenCaptureBounds(0, 0, 1920, 1080);

        var success = UiAutomationExecutor.TryResolveCaptureBounds(
            monitors, virtualBounds, null, 1800, 900, 300, 300,
            out _, out _, out _, out var error);

        Assert.False(success);
        Assert.Contains("virtual desktop", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullDesktop_ReturnsPngAndMonitorMetadata()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.CaptureScreenScreenshotAsync(
            null, null, null, null, null, 1280, 720, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.NotEmpty(result.Data!.Monitors);
        Assert.All(result.Data.Monitors, monitor =>
        {
            Assert.True(monitor.DpiX > 0);
            Assert.True(monitor.DpiY > 0);
        });
        Assert.True(result.Data.ByteLength > 8);
        Assert.StartsWith("iVBOR", result.Data.PngBase64, StringComparison.Ordinal);
    }
}
