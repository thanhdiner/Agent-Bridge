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
public sealed class ScreenCaptureTools
{
    private const int MaximumScreenshotPngBytes = 6 * 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<ScreenCaptureTools> _logger;

    public ScreenCaptureTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<ScreenCaptureTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "screen_screenshot",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Captures the composed Windows virtual desktop, one monitor, or one virtual-screen region as an in-memory PNG. Includes monitor bounds, work areas, and DPI metadata. Does not write a file. Requires dev:execute scope.")]
    public async Task<CallToolResult> CaptureScreenAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("Optional zero-based monitor index. Omit to capture the full virtual desktop. Cannot be combined with region coordinates.")] int? monitorIndex = null,
        [Description("Optional region left coordinate in virtual-screen pixels. Supply x, y, width, and height together.")] int? x = null,
        [Description("Optional region top coordinate in virtual-screen pixels. Supply x, y, width, and height together.")] int? y = null,
        [Description("Optional region width in pixels. Supply x, y, width, and height together.")] int? width = null,
        [Description("Optional region height in pixels. Supply x, y, width, and height together.")] int? height = null,
        [Description("Maximum output width in pixels (default: 4096, hard limit: 4096)")] int maxWidth = 4096,
        [Description("Maximum output height in pixels (default: 4096, hard limit: 4096)")] int maxHeight = 4096)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(deviceId))
            return Error("INVALID_REQUEST", "deviceId parameter is required.");
        if (maxWidth is < 1 or > 4096 || maxHeight is < 1 or > 4096)
            return Error("INVALID_REQUEST", "maxWidth and maxHeight must be between 1 and 4096.");

        var command = new ScreenScreenshotCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            MonitorIndex = monitorIndex,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight
        };

        try
        {
            var result = await _dispatcher.SendAsync<ScreenScreenshotResult>(command, CancellationToken());
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
            _logger.LogError(ex, "Unexpected error executing screen_screenshot for device {DeviceId}", deviceId);
            return Error("INTERNAL_ERROR", "An unexpected error occurred on the gateway.");
        }
    }

    internal static CallToolResult BuildScreenshotResult(ScreenScreenshotResult result)
    {
        byte[] png;
        try
        {
            png = Convert.FromBase64String(result.PngBase64);
        }
        catch (FormatException)
        {
            return Error("SCREEN_SCREENSHOT_FAILED", "The agent returned invalid screenshot data.");
        }

        if (!string.Equals(result.MimeType, "image/png", StringComparison.OrdinalIgnoreCase) ||
            png.Length is < 8 or > MaximumScreenshotPngBytes ||
            png.Length != result.ByteLength ||
            !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            return Error("SCREEN_SCREENSHOT_FAILED", "The screenshot payload is invalid.");
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        if (!string.Equals(sha256, result.Sha256, StringComparison.OrdinalIgnoreCase))
            return Error("SCREEN_SCREENSHOT_FAILED", "The screenshot payload failed integrity verification.");

        var metadata = new
        {
            result.CaptureMode,
            result.SelectedMonitorIndex,
            result.Bounds,
            result.VirtualScreenBounds,
            result.Monitors,
            result.OriginalWidth,
            result.OriginalHeight,
            result.Width,
            result.Height,
            result.Scaled,
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
