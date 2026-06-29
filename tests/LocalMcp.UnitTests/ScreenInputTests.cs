using System.Reflection;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Gateway.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class ScreenInputTests
{
    [Fact]
    public void Tools_ExposeExpectedNamesAndSafetyMetadata()
    {
        var methods = typeof(ScreenInputTools).GetMethods()
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttribute<McpServerToolAttribute>()
            })
            .Where(item => item.Attribute is not null)
            .ToDictionary(item => item.Attribute!.Name!, item => item);

        Assert.Equal(
            new[] { "screen_click", "screen_double_click", "screen_drag", "screen_right_click", "screen_scroll" },
            methods.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray());

        Assert.All(methods.Values, item =>
        {
            Assert.False(item.Attribute!.ReadOnly);
            Assert.False(item.Attribute.Destructive);
            Assert.False(item.Attribute.Idempotent);
            Assert.False(item.Attribute.OpenWorld);
        });
    }

    [Theory]
    [InlineData("screen_click")]
    [InlineData("screen_double_click")]
    [InlineData("screen_right_click")]
    public void ClickTools_RequireForegroundGuard(string toolName)
    {
        var method = typeof(ScreenInputTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        var names = method.GetParameters().Select(parameter => parameter.Name).ToArray();

        Assert.Equal(
            new[] { "deviceId", "expectedForegroundWindowHandle", "x", "y", "monitorIndex", "expectedProcessId", "expectedWindowTitle" },
            names);
    }

    [Fact]
    public void ResolvePoint_SupportsNegativeCoordinatesAndMonitorGuard()
    {
        var monitors = CreateTwoMonitorTopology();
        var virtualBounds = new UiAutomationExecutor.ScreenCaptureBounds(-1280, 0, 3200, 1080);

        var success = UiAutomationExecutor.TryResolveScreenInputPoint(
            monitors, virtualBounds, -640, 400, 1,
            out var monitorIndex, out var error);

        Assert.True(success, error?.Message);
        Assert.Equal(1, monitorIndex);
        Assert.Null(error);
    }

    [Fact]
    public void ResolvePoint_RejectsMonitorMismatch()
    {
        var monitors = CreateTwoMonitorTopology();
        var virtualBounds = new UiAutomationExecutor.ScreenCaptureBounds(-1280, 0, 3200, 1080);

        var success = UiAutomationExecutor.TryResolveScreenInputPoint(
            monitors, virtualBounds, 500, 400, 1,
            out var monitorIndex, out var error);

        Assert.False(success);
        Assert.Equal(0, monitorIndex);
        Assert.Equal(ErrorCodes.ScreenMonitorMismatch, error?.Code);
    }

    [Fact]
    public void ResolvePoint_RejectsGapBetweenMonitors()
    {
        IReadOnlyList<UiAutomationExecutor.ScreenMonitorSnapshot> monitors =
        [
            Monitor(0, "PRIMARY", true, 0, 0, 1000, 800, 96),
            Monitor(1, "RIGHT", false, 1200, 0, 1000, 800, 120)
        ];
        var virtualBounds = new UiAutomationExecutor.ScreenCaptureBounds(0, 0, 2200, 800);

        var success = UiAutomationExecutor.TryResolveScreenInputPoint(
            monitors, virtualBounds, 1100, 400, null,
            out _, out var error);

        Assert.False(success);
        Assert.Equal(ErrorCodes.ScreenPointOutOfBounds, error?.Code);
        Assert.Contains("gap", error?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePoints_SupportsCrossMonitorDrag()
    {
        var monitors = CreateTwoMonitorTopology();
        var virtualBounds = new UiAutomationExecutor.ScreenCaptureBounds(-1280, 0, 3200, 1080);

        var startOk = UiAutomationExecutor.TryResolveScreenInputPoint(
            monitors, virtualBounds, 100, 100, 0,
            out var startMonitor, out var startError);
        var endOk = UiAutomationExecutor.TryResolveScreenInputPoint(
            monitors, virtualBounds, -500, 100, 1,
            out var endMonitor, out var endError);

        Assert.True(startOk, startError?.Message);
        Assert.True(endOk, endError?.Message);
        Assert.Equal(0, startMonitor);
        Assert.Equal(1, endMonitor);
    }

    [Theory]
    [InlineData(ScreenScrollDirections.Up, 3, 360)]
    [InlineData(ScreenScrollDirections.Down, 3, -360)]
    [InlineData(ScreenScrollDirections.Right, 2, 240)]
    [InlineData(ScreenScrollDirections.Left, 2, -240)]
    public void WheelDelta_HasExpectedDirection(string direction, int notches, int expected)
    {
        Assert.Equal(expected, UiAutomationExecutor.GetScreenWheelDelta(direction, notches));
    }

    [Fact]
    public async Task ClickScreen_InvalidHandle_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.ClickScreenAsync(
            "invalid", 10, 10, null, WindowMouseButtons.Left, 1,
            null, null, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task DragScreen_InvalidMonitorIndex_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.DragScreenAsync(
            "0x1234", 10, 10, 20, 20, -1, null,
            WindowMouseButtons.Left, 100, 10, null, null,
            Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task ScrollScreen_InvalidDirection_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.ScrollScreenAsync(
            "0x1234", 10, 10, null, "diagonal", 3,
            null, null, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    private static IReadOnlyList<UiAutomationExecutor.ScreenMonitorSnapshot> CreateTwoMonitorTopology() =>
    [
        Monitor(0, "PRIMARY", true, 0, 0, 1920, 1080, 144),
        Monitor(1, "LEFT", false, -1280, 0, 1280, 1024, 96)
    ];

    private static UiAutomationExecutor.ScreenMonitorSnapshot Monitor(
        int index,
        string name,
        bool primary,
        int x,
        int y,
        int width,
        int height,
        uint dpi) =>
        new(
            index,
            name,
            primary,
            new UiAutomationExecutor.ScreenCaptureBounds(x, y, width, height),
            new UiAutomationExecutor.ScreenCaptureBounds(x, y, width, Math.Max(1, height - 40)),
            dpi,
            dpi);
}
