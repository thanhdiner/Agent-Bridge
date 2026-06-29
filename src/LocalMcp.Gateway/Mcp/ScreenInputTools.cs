using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.Gateway.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalMcp.Gateway.Mcp;

[McpServerToolType]
public sealed class ScreenInputTools
{
    private const int MaximumCoordinate = 100000;
    private const int MaximumTitleLength = 1024;

    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<ScreenInputTools> _logger;

    public ScreenInputTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<ScreenInputTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(Name = "screen_click", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Clicks one virtual-desktop point only when the guarded Windows window is still foreground. Requires dev:execute scope.")]
    public Task<CallToolResult> ClickAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("Required native handle of the window expected to own the foreground")] string expectedForegroundWindowHandle,
        [Description("Horizontal coordinate in virtual-desktop pixels; may be negative")] int x,
        [Description("Vertical coordinate in virtual-desktop pixels; may be negative")] int y,
        [Description("Optional zero-based monitor index guard")] int? monitorIndex = null,
        [Description("Optional exact process id guard")] int? expectedProcessId = null,
        [Description("Optional exact window title guard")] string? expectedWindowTitle = null) =>
        ExecuteClickAsync(deviceId, expectedForegroundWindowHandle, x, y, monitorIndex,
            WindowMouseButtons.Left, 1, expectedProcessId, expectedWindowTitle, "screen_click");

    [McpServerTool(Name = "screen_double_click", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Double-clicks one virtual-desktop point only when the guarded Windows window is still foreground. Requires dev:execute scope.")]
    public Task<CallToolResult> DoubleClickAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("Required native handle of the window expected to own the foreground")] string expectedForegroundWindowHandle,
        [Description("Horizontal coordinate in virtual-desktop pixels; may be negative")] int x,
        [Description("Vertical coordinate in virtual-desktop pixels; may be negative")] int y,
        [Description("Optional zero-based monitor index guard")] int? monitorIndex = null,
        [Description("Optional exact process id guard")] int? expectedProcessId = null,
        [Description("Optional exact window title guard")] string? expectedWindowTitle = null) =>
        ExecuteClickAsync(deviceId, expectedForegroundWindowHandle, x, y, monitorIndex,
            WindowMouseButtons.Left, 2, expectedProcessId, expectedWindowTitle, "screen_double_click");

    [McpServerTool(Name = "screen_right_click", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Right-clicks one virtual-desktop point only when the guarded Windows window is still foreground. Requires dev:execute scope.")]
    public Task<CallToolResult> RightClickAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("Required native handle of the window expected to own the foreground")] string expectedForegroundWindowHandle,
        [Description("Horizontal coordinate in virtual-desktop pixels; may be negative")] int x,
        [Description("Vertical coordinate in virtual-desktop pixels; may be negative")] int y,
        [Description("Optional zero-based monitor index guard")] int? monitorIndex = null,
        [Description("Optional exact process id guard")] int? expectedProcessId = null,
        [Description("Optional exact window title guard")] string? expectedWindowTitle = null) =>
        ExecuteClickAsync(deviceId, expectedForegroundWindowHandle, x, y, monitorIndex,
            WindowMouseButtons.Right, 1, expectedProcessId, expectedWindowTitle, "screen_right_click");

    [McpServerTool(Name = "screen_drag", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Drags between two absolute virtual-desktop points while continuously enforcing the foreground-window guard. Requires dev:execute scope.")]
    public async Task<CallToolResult> DragAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("Required native handle of the window expected to own the foreground")] string expectedForegroundWindowHandle,
        [Description("Absolute virtual-desktop start X coordinate; may be negative")] int startX,
        [Description("Absolute virtual-desktop start Y coordinate; may be negative")] int startY,
        [Description("Absolute virtual-desktop end X coordinate; may be negative")] int endX,
        [Description("Absolute virtual-desktop end Y coordinate; may be negative")] int endY,
        [Description("Mouse button: left, right, or middle")] string button = WindowMouseButtons.Left,
        [Description("Total duration in milliseconds, from 0 to 10000")] int durationMs = 300,
        [Description("Interpolated movement steps, from 1 to 240")] int steps = 20,
        [Description("Optional zero-based start monitor index guard")] int? startMonitorIndex = null,
        [Description("Optional zero-based end monitor index guard")] int? endMonitorIndex = null,
        [Description("Optional exact process id guard")] int? expectedProcessId = null,
        [Description("Optional exact window title guard")] string? expectedWindowTitle = null)
    {
        var commonError = await ValidateCommonAsync(deviceId, expectedForegroundWindowHandle, expectedProcessId, expectedWindowTitle);
        if (commonError is not null)
            return commonError;
        if (!ValidCoordinate(startX) || !ValidCoordinate(startY) || !ValidCoordinate(endX) || !ValidCoordinate(endY))
            return Error("INVALID_REQUEST", $"All coordinates must be between {-MaximumCoordinate} and {MaximumCoordinate}.");
        if (startMonitorIndex is < 0 || endMonitorIndex is < 0)
            return Error("INVALID_REQUEST", "Monitor indexes must be zero or greater when provided.");

        var normalizedButton = button?.Trim().ToLowerInvariant();
        if (!WindowMouseButtons.IsSupported(normalizedButton))
            return Error("INVALID_REQUEST", "button must be left, right, or middle.");
        if (durationMs is < 0 or > 10000)
            return Error("INVALID_REQUEST", "durationMs must be between 0 and 10000.");
        if (steps is < 1 or > 240)
            return Error("INVALID_REQUEST", "steps must be between 1 and 240.");

        var command = new ScreenDragCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpectedForegroundWindowHandle = expectedForegroundWindowHandle,
            StartX = startX,
            StartY = startY,
            EndX = endX,
            EndY = endY,
            StartMonitorIndex = startMonitorIndex,
            EndMonitorIndex = endMonitorIndex,
            Button = normalizedButton!,
            DurationMs = durationMs,
            Steps = steps,
            ExpectedProcessId = expectedProcessId,
            ExpectedWindowTitle = expectedWindowTitle
        };

        return await DispatchAsync<ScreenDragResult>(command, "screen_drag", deviceId);
    }

    [McpServerTool(Name = "screen_scroll", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Sends vertical or horizontal mouse-wheel input at one guarded virtual-desktop point. Requires dev:execute scope.")]
    public async Task<CallToolResult> ScrollAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("Required native handle of the window expected to own the foreground")] string expectedForegroundWindowHandle,
        [Description("Horizontal coordinate in virtual-desktop pixels; may be negative")] int x,
        [Description("Vertical coordinate in virtual-desktop pixels; may be negative")] int y,
        [Description("Direction: up, down, left, or right")] string direction,
        [Description("Wheel notches from 1 to 20")] int notches = 3,
        [Description("Optional zero-based monitor index guard")] int? monitorIndex = null,
        [Description("Optional exact process id guard")] int? expectedProcessId = null,
        [Description("Optional exact window title guard")] string? expectedWindowTitle = null)
    {
        var commonError = await ValidateCommonAsync(deviceId, expectedForegroundWindowHandle, expectedProcessId, expectedWindowTitle);
        if (commonError is not null)
            return commonError;
        if (!ValidCoordinate(x) || !ValidCoordinate(y))
            return Error("INVALID_REQUEST", $"x and y must be between {-MaximumCoordinate} and {MaximumCoordinate}.");
        if (monitorIndex is < 0)
            return Error("INVALID_REQUEST", "monitorIndex must be zero or greater when provided.");
        if (!ScreenScrollDirections.TryNormalize(direction, out var normalizedDirection))
            return Error("INVALID_REQUEST", "direction must be up, down, left, or right.");
        if (notches is < 1 or > 20)
            return Error("INVALID_REQUEST", "notches must be between 1 and 20.");

        var command = new ScreenScrollCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpectedForegroundWindowHandle = expectedForegroundWindowHandle,
            X = x,
            Y = y,
            MonitorIndex = monitorIndex,
            Direction = normalizedDirection,
            Notches = notches,
            ExpectedProcessId = expectedProcessId,
            ExpectedWindowTitle = expectedWindowTitle
        };

        return await DispatchAsync<ScreenScrollResult>(command, "screen_scroll", deviceId);
    }

    private async Task<CallToolResult> ExecuteClickAsync(
        string deviceId,
        string expectedForegroundWindowHandle,
        int x,
        int y,
        int? monitorIndex,
        string button,
        int clickCount,
        int? expectedProcessId,
        string? expectedWindowTitle,
        string toolName)
    {
        var commonError = await ValidateCommonAsync(deviceId, expectedForegroundWindowHandle, expectedProcessId, expectedWindowTitle);
        if (commonError is not null)
            return commonError;
        if (!ValidCoordinate(x) || !ValidCoordinate(y))
            return Error("INVALID_REQUEST", $"x and y must be between {-MaximumCoordinate} and {MaximumCoordinate}.");
        if (monitorIndex is < 0)
            return Error("INVALID_REQUEST", "monitorIndex must be zero or greater when provided.");

        var command = new ScreenClickCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpectedForegroundWindowHandle = expectedForegroundWindowHandle,
            X = x,
            Y = y,
            MonitorIndex = monitorIndex,
            Button = button,
            ClickCount = clickCount,
            ExpectedProcessId = expectedProcessId,
            ExpectedWindowTitle = expectedWindowTitle
        };

        return await DispatchAsync<ScreenClickResult>(command, toolName, deviceId);
    }

    private async Task<CallToolResult?> ValidateCommonAsync(
        string deviceId,
        string expectedForegroundWindowHandle,
        int? expectedProcessId,
        string? expectedWindowTitle)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(deviceId))
            return Error("INVALID_REQUEST", "deviceId parameter is required.");
        if (!ValidHandle(expectedForegroundWindowHandle))
            return Error("INVALID_REQUEST", "expectedForegroundWindowHandle is invalid.");
        if (expectedProcessId is <= 0)
            return Error("INVALID_REQUEST", "expectedProcessId must be greater than zero when provided.");
        if (expectedWindowTitle is not null
            && (expectedWindowTitle.Length > MaximumTitleLength || expectedWindowTitle.Any(char.IsControl)))
        {
            return Error("INVALID_REQUEST", "expectedWindowTitle is invalid.");
        }

        return null;
    }

    private async Task<CallToolResult> DispatchAsync<T>(AgentCommand command, string toolName, string deviceId)
    {
        try
        {
            var result = await _dispatcher.SendAsync<T>(command, RequestToken());
            if (result.Success && result.Data is not null)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result.Data, JsonOptions.Default) }],
                    IsError = false
                };
            }

            return Error(result.Error?.Code ?? "INTERNAL_ERROR", result.Error?.Message ?? "Command execution failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected {ToolName} failure for device {DeviceId}", toolName, deviceId);
            return Error("INTERNAL_ERROR", "An unexpected error occurred on the gateway.");
        }
    }

    private async Task<bool> AuthorizedAsync()
    {
        var principal = _httpContextAccessor?.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        return (await _authorizationService.AuthorizeAsync(principal, null, "DevExecutePolicy")).Succeeded;
    }

    private CancellationToken RequestToken() =>
        _httpContextAccessor?.HttpContext?.RequestAborted ?? CancellationToken.None;

    private static bool ValidCoordinate(int value) => value is >= -MaximumCoordinate and <= MaximumCoordinate;

    private static bool ValidHandle(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 32
        && !value.Any(char.IsControl);

    private static CallToolResult Error(string code, string message) => new()
    {
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
        IsError = true
    };
}
