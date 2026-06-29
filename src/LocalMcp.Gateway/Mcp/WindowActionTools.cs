using System.ComponentModel;
using System.Security.Claims;
using System.Security.Cryptography;
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
public sealed class WindowActionTools
{
    private const int MaximumScreenshotPngBytes = 6 * 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<WindowActionTools> _logger;

    public WindowActionTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<WindowActionTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "window_focus",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Brings one live top-level Windows window to the foreground and restores it first when minimized. Requires dev:execute scope.")]
    public async Task<CallToolResult> FocusWindowAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle)
    {
        var validation = await ValidateAsync(deviceId, windowHandle);
        if (validation is not null)
            return validation;

        return await DispatchAsync<WindowFocusResult>(
            new WindowFocusCommand
            {
                CommandId = Guid.NewGuid(),
                DeviceId = deviceId,
                CreatedAt = DateTimeOffset.UtcNow,
                WindowHandle = windowHandle
            },
            "window_focus");
    }

    [McpServerTool(
        Name = "window_move",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Moves and resizes one live top-level Windows window. Minimized or maximized windows can be restored before applying bounds. Requires dev:execute scope.")]
    public async Task<CallToolResult> MoveWindowAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("Target left coordinate in virtual-screen pixels")] int x,
        [Description("Target top coordinate in virtual-screen pixels")] int y,
        [Description("Target width in pixels, between 1 and 100000")] int width,
        [Description("Target height in pixels, between 1 and 100000")] int height,
        [Description("Whether to restore a minimized or maximized window before moving it (default: true)")] bool restoreIfNeeded = true)
    {
        var validation = await ValidateAsync(deviceId, windowHandle);
        if (validation is not null)
            return validation;
        if (width is < 1 or > 100000 || height is < 1 or > 100000)
            return Error("INVALID_REQUEST", "width and height must be between 1 and 100000.");
        if (x is < -100000 or > 100000 || y is < -100000 or > 100000)
            return Error("INVALID_REQUEST", "x and y must be between -100000 and 100000.");

        return await DispatchAsync<WindowMoveResult>(
            new WindowMoveCommand
            {
                CommandId = Guid.NewGuid(),
                DeviceId = deviceId,
                CreatedAt = DateTimeOffset.UtcNow,
                WindowHandle = windowHandle,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                RestoreIfNeeded = restoreIfNeeded
            },
            "window_move");
    }

    [McpServerTool(
        Name = "window_screenshot",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Captures one live top-level Windows window as a bounded PNG image. Uses off-screen rendering first and a screen-region fallback when necessary. Does not write a file. Requires dev:execute scope.")]
    public async Task<CallToolResult> CaptureWindowAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("Maximum output width in pixels (default: 1920, hard limit: 4096)")] int maxWidth = 1920,
        [Description("Maximum output height in pixels (default: 1080, hard limit: 4096)")] int maxHeight = 1080)
    {
        var validation = await ValidateAsync(deviceId, windowHandle);
        if (validation is not null)
            return validation;
        if (maxWidth is < 1 or > 4096 || maxHeight is < 1 or > 4096)
            return Error("INVALID_REQUEST", "maxWidth and maxHeight must be between 1 and 4096.");

        var command = new WindowScreenshotCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = windowHandle,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight
        };

        try
        {
            var result = await _dispatcher.SendAsync<WindowScreenshotResult>(command, CancellationToken());
            if (!result.Success || result.Data is null)
            {
                return Error(
                    result.Error?.Code ?? "INTERNAL_ERROR",
                    result.Error?.Message ?? "An unexpected error occurred during command execution.");
            }

            return BuildScreenshotResult(result.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing window_screenshot for device {DeviceId}", deviceId);
            return Error("INTERNAL_ERROR", "An unexpected error occurred on the gateway.");
        }
    }

    private static CallToolResult BuildScreenshotResult(WindowScreenshotResult result)
    {
        byte[] png;
        try
        {
            png = Convert.FromBase64String(result.PngBase64);
        }
        catch (FormatException)
        {
            return Error("WINDOW_SCREENSHOT_FAILED", "The agent returned invalid screenshot data.");
        }

        if (!string.Equals(result.MimeType, "image/png", StringComparison.OrdinalIgnoreCase) ||
            png.Length is < 8 or > MaximumScreenshotPngBytes ||
            png.Length != result.ByteLength ||
            !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            return Error("WINDOW_SCREENSHOT_FAILED", "The screenshot payload is invalid.");
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        if (!string.Equals(sha256, result.Sha256, StringComparison.OrdinalIgnoreCase))
            return Error("WINDOW_SCREENSHOT_FAILED", "The screenshot payload failed integrity verification.");

        var metadata = new
        {
            result.WindowHandle,
            result.Title,
            result.ProcessId,
            result.ProcessName,
            result.Bounds,
            result.OriginalWidth,
            result.OriginalHeight,
            result.Width,
            result.Height,
            result.Scaled,
            result.WasMinimized,
            result.CaptureMethod,
            result.MimeType,
            result.ByteLength,
            result.Sha256
        };

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = JsonSerializer.Serialize(metadata, JsonOptions.Default) },
                ImageContentBlock.FromBytes(png, "image/png")
            ],
            IsError = false
        };
    }

    private async Task<CallToolResult?> ValidateAsync(string deviceId, string windowHandle)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(deviceId))
            return Error("INVALID_REQUEST", "deviceId parameter is required.");
        if (string.IsNullOrWhiteSpace(windowHandle) || windowHandle.Length > 32 || windowHandle.Any(char.IsControl))
            return Error("INVALID_REQUEST", "windowHandle is required and must be at most 32 characters without control characters.");
        return null;
    }

    private async Task<CallToolResult> DispatchAsync<TResult>(AgentCommand command, string toolName)
    {
        try
        {
            var result = await _dispatcher.SendAsync<TResult>(command, CancellationToken());
            if (result.Success && result.Data is not null)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result.Data, JsonOptions.Default) }],
                    IsError = false
                };
            }

            return Error(
                result.Error?.Code ?? "INTERNAL_ERROR",
                result.Error?.Message ?? "An unexpected error occurred during command execution.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing {ToolName} for device {DeviceId}", toolName, command.DeviceId);
            return Error("INTERNAL_ERROR", "An unexpected error occurred on the gateway.");
        }
    }

    private async Task<bool> AuthorizedAsync()
    {
        var principal = _httpContextAccessor?.HttpContext?.User
            ?? new ClaimsPrincipal(new ClaimsIdentity());
        return (await _authorizationService.AuthorizeAsync(principal, null, "DevExecutePolicy")).Succeeded;
    }

    private CancellationToken CancellationToken() =>
        _httpContextAccessor?.HttpContext?.RequestAborted ?? System.Threading.CancellationToken.None;

    private static CallToolResult Error(string code, string message) =>
        new()
        {
            Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
            IsError = true
        };
}
