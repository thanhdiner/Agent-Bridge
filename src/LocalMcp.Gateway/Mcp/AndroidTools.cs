using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.Gateway.Commands;
using LocalMcp.Gateway.Connections;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalMcp.Gateway.Mcp;

[McpServerToolType]
public sealed partial class AndroidTools
{
    private const int MaximumCoordinate = 100_000;
    private const int MaximumScreenshotBytes = 6 * 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly HashSet<string> AllowedKeyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BACK", "HOME", "APP_SWITCH", "ENTER", "ESCAPE", "TAB", "SPACE", "DEL", "FORWARD_DEL",
        "DPAD_UP", "DPAD_DOWN", "DPAD_LEFT", "DPAD_RIGHT", "DPAD_CENTER", "MOVE_HOME", "MOVE_END",
        "PAGE_UP", "PAGE_DOWN", "VOLUME_UP", "VOLUME_DOWN", "VOLUME_MUTE", "MEDIA_PLAY_PAUSE",
        "MEDIA_NEXT", "MEDIA_PREVIOUS"
    };

    private readonly ICommandDispatcher _dispatcher;
    private readonly IAgentConnectionRegistry _registry;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<AndroidTools> _logger;

    public AndroidTools(
        ICommandDispatcher dispatcher,
        IAgentConnectionRegistry registry,
        IAuthorizationService authorizationService,
        ILogger<AndroidTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _registry = registry;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(Name = "android_device_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists Android devices currently connected through an AgentBridge Android ADB agent.")]
    public async Task<CallToolResult> ListDevicesAsync()
    {
        if (!await AuthorizedAsync("McpAuthenticatedPolicy"))
            return Error("FORBIDDEN", "Authentication is required.");

        var devices = _registry.GetActiveDeviceInfos()
            .Where(device => string.Equals(device.Platform, "android", StringComparison.OrdinalIgnoreCase))
            .OrderBy(device => device.DisplayName ?? device.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(device => new
            {
                device.DeviceId,
                device.DisplayName,
                device.Platform,
                Capabilities = device.Capabilities ?? Array.Empty<string>(),
                Online = true,
                device.ConnectedAtUtc
            })
            .ToArray();
        return Json(new { Count = devices.Length, Devices = devices });
    }

    [McpServerTool(Name = "android_get_state", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns Android OS, screen, and foreground-app state through ADB.")]
    public Task<CallToolResult> GetStateAsync(
        [Description("Optional Android device id. Omit when exactly one Android device is connected."), Optional, DefaultParameterValue(null)] string? deviceId) =>
        DispatchAsync<AndroidDeviceStateResult>(deviceId, resolved => new AndroidGetStateCommand
        {
            CommandId = Guid.NewGuid(), DeviceId = resolved, CreatedAt = DateTimeOffset.UtcNow
        }, "android_get_state", "android.state");

    [McpServerTool(Name = "android_screenshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Captures the current Android display as an in-memory PNG through ADB.")]
    public async Task<CallToolResult> ScreenshotAsync(
        [Description("Optional Android device id. Omit when exactly one Android device is connected."), Optional, DefaultParameterValue(null)] string? deviceId)
    {
        var validation = await ValidateTargetAsync(deviceId, "android.screenshot");
        if (validation.Error is not null)
            return validation.Error;

        try
        {
            var result = await _dispatcher.SendAsync<AndroidScreenshotResult>(new AndroidScreenshotCommand
            {
                CommandId = Guid.NewGuid(), DeviceId = validation.DeviceId!, CreatedAt = DateTimeOffset.UtcNow
            }, RequestToken());
            if (!result.Success || result.Data is null)
                return Error(result.Error?.Code ?? "INTERNAL_ERROR", result.Error?.Message ?? "Android screenshot failed.");
            return BuildScreenshotResult(result.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected android_screenshot failure for device {DeviceId}", deviceId);
            return Error("INTERNAL_ERROR", "An unexpected error occurred on the Gateway.");
        }
    }

    [McpServerTool(Name = "android_ui_tree", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Dumps the current Android UIAutomator hierarchy as XML. Password text is normally redacted by Android.")]
    public async Task<CallToolResult> UiTreeAsync(
        [Description("Optional Android device id. Omit when exactly one Android device is connected."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Maximum returned XML characters, between 1000 and 500000.")] int maxCharacters = 200_000)
    {
        if (maxCharacters is < 1_000 or > 500_000)
            return Error("INVALID_REQUEST", "maxCharacters must be between 1000 and 500000.");
        return await DispatchAsync<AndroidUiTreeResult>(deviceId, resolved => new AndroidUiTreeCommand
        {
            CommandId = Guid.NewGuid(), DeviceId = resolved, CreatedAt = DateTimeOffset.UtcNow, MaxCharacters = maxCharacters
        }, "android_ui_tree", "android.ui_tree");
    }

    [McpServerTool(Name = "android_tap", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Taps an absolute pixel coordinate on the Android display.")]
    public async Task<CallToolResult> TapAsync(
        [Description("Optional Android device id. Omit when exactly one Android device is connected."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Horizontal screen coordinate in pixels.")] int x,
        [Description("Vertical screen coordinate in pixels.")] int y)
    {
        if (!ValidCoordinate(x) || !ValidCoordinate(y))
            return Error("INVALID_REQUEST", $"x and y must be between 0 and {MaximumCoordinate}.");
        return await DispatchAsync<AndroidInputResult>(deviceId, resolved => new AndroidTapCommand
        {
            CommandId = Guid.NewGuid(), DeviceId = resolved, CreatedAt = DateTimeOffset.UtcNow, X = x, Y = y
        }, "android_tap", "android.tap");
    }

    [McpServerTool(Name = "android_swipe", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Swipes between two absolute pixel coordinates on the Android display.")]
    public async Task<CallToolResult> SwipeAsync(
        [Description("Optional Android device id. Omit when exactly one Android device is connected."), Optional, DefaultParameterValue(null)] string? deviceId,
        int startX,
        int startY,
        int endX,
        int endY,
        [Description("Swipe duration in milliseconds, from 0 to 10000.")] int durationMs = 300)
    {
        if (!new[] { startX, startY, endX, endY }.All(ValidCoordinate))
            return Error("INVALID_REQUEST", $"All coordinates must be between 0 and {MaximumCoordinate}.");
        if (durationMs is < 0 or > 10_000)
            return Error("INVALID_REQUEST", "durationMs must be between 0 and 10000.");
        return await DispatchAsync<AndroidInputResult>(deviceId, resolved => new AndroidSwipeCommand
        {
            CommandId = Guid.NewGuid(), DeviceId = resolved, CreatedAt = DateTimeOffset.UtcNow,
            StartX = startX, StartY = startY, EndX = endX, EndY = endY, DurationMs = durationMs
        }, "android_swipe", "android.swipe");
    }

    [McpServerTool(Name = "android_type_text", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Types safe printable ASCII text into the currently focused Android field. Unicode is not supported by standard ADB input.")]
    public async Task<CallToolResult> TypeTextAsync(
        [Description("Optional Android device id. Omit when exactly one Android device is connected."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Text containing letters, digits, spaces, and .,_@:+-/% only; maximum 2000 characters.")] string text)
    {
        if (!IsSafeAdbText(text))
            return Error("INVALID_REQUEST", "text contains unsupported characters or exceeds 2000 characters.");
        return await DispatchAsync<AndroidInputResult>(deviceId, resolved => new AndroidTypeTextCommand
        {
            CommandId = Guid.NewGuid(), DeviceId = resolved, CreatedAt = DateTimeOffset.UtcNow, Text = text
        }, "android_type_text", "android.type_text");
    }

    [McpServerTool(Name = "android_press_key", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Presses an allowlisted Android key such as BACK, HOME, ENTER, APP_SWITCH, or a DPAD key.")]
    public async Task<CallToolResult> PressKeyAsync(
        [Description("Optional Android device id. Omit when exactly one Android device is connected."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Allowlisted Android key name, with or without the KEYCODE_ prefix.")] string keyCode)
    {
        var normalized = NormalizeKeyCode(keyCode);
        if (!AllowedKeyCodes.Contains(normalized))
            return Error("INVALID_REQUEST", $"keyCode '{keyCode}' is not allowed.");
        return await DispatchAsync<AndroidInputResult>(deviceId, resolved => new AndroidPressKeyCommand
        {
            CommandId = Guid.NewGuid(), DeviceId = resolved, CreatedAt = DateTimeOffset.UtcNow, KeyCode = normalized
        }, "android_press_key", "android.press_key");
    }

    [McpServerTool(Name = "android_open_app", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Starts an installed Android app by package name and optional activity.")]
    public async Task<CallToolResult> OpenAppAsync(
        [Description("Optional Android device id. Omit when exactly one Android device is connected."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Android package name, for example com.android.settings.")] string packageName,
        [Description("Optional activity class, for example .Settings or com.example.MainActivity."), Optional, DefaultParameterValue(null)] string? activity)
    {
        if (!PackageRegex().IsMatch(packageName))
            return Error("INVALID_REQUEST", "packageName is invalid.");
        if (activity is not null && !ActivityRegex().IsMatch(activity))
            return Error("INVALID_REQUEST", "activity is invalid.");
        return await DispatchAsync<AndroidOpenAppResult>(deviceId, resolved => new AndroidOpenAppCommand
        {
            CommandId = Guid.NewGuid(), DeviceId = resolved, CreatedAt = DateTimeOffset.UtcNow,
            PackageName = packageName, Activity = activity
        }, "android_open_app", "android.open_app");
    }

    private async Task<CallToolResult> DispatchAsync<TResult>(
        string? deviceId,
        Func<string, AgentCommand> commandFactory,
        string toolName,
        string? capability = null)
    {
        var validation = await ValidateTargetAsync(deviceId, capability ?? toolName.Replace('_', '.'));
        if (validation.Error is not null)
            return validation.Error;

        try
        {
            var result = await _dispatcher.SendAsync<TResult>(commandFactory(validation.DeviceId!), RequestToken());
            return result.Success && result.Data is not null
                ? Json(result.Data)
                : Error(result.Error?.Code ?? "INTERNAL_ERROR", result.Error?.Message ?? "Android command failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected {ToolName} failure for Android device {DeviceId}", toolName, deviceId);
            return Error("INTERNAL_ERROR", "An unexpected error occurred on the Gateway.");
        }
    }

    private async Task<(string? DeviceId, CallToolResult? Error)> ValidateTargetAsync(string? requestedDeviceId, string capability)
    {
        if (!await AuthorizedAsync("DevExecutePolicy"))
            return (null, Error("FORBIDDEN", "Access denied. Required scope: dev:execute"));

        var androidDevices = _registry.GetActiveDeviceInfos()
            .Where(device => string.Equals(device.Platform, "android", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        AgentDeviceInfo? target;
        if (string.IsNullOrWhiteSpace(requestedDeviceId))
        {
            if (androidDevices.Length == 0)
                return (null, Error("NO_ANDROID_DEVICE", "No Android ADB agent is connected."));
            if (androidDevices.Length > 1)
                return (null, Error("MULTIPLE_ANDROID_DEVICES", "More than one Android device is connected; provide deviceId."));
            target = androidDevices[0];
        }
        else
        {
            target = _registry.GetDevice(requestedDeviceId.Trim());
            if (target is null)
                return (null, Error("ANDROID_DEVICE_OFFLINE", $"Android device '{requestedDeviceId.Trim()}' is not connected."));
            if (!string.Equals(target.Platform, "android", StringComparison.OrdinalIgnoreCase))
                return (null, Error("WRONG_DEVICE_PLATFORM", $"Device '{target.DeviceId}' is not an Android device."));
        }

        if (target.Capabilities is { Count: > 0 }
            && !target.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
        {
            return (null, Error("ANDROID_CAPABILITY_UNAVAILABLE", $"Android device '{target.DeviceId}' does not advertise capability '{capability}'."));
        }

        return (target.DeviceId, null);
    }

    internal static CallToolResult BuildScreenshotResult(AndroidScreenshotResult result)
    {
        byte[] png;
        try
        {
            png = Convert.FromBase64String(result.PngBase64);
        }
        catch (FormatException)
        {
            return Error("ANDROID_SCREENSHOT_FAILED", "The Android agent returned invalid screenshot data.");
        }

        if (!string.Equals(result.MimeType, "image/png", StringComparison.OrdinalIgnoreCase)
            || png.Length is < 24 or > MaximumScreenshotBytes
            || png.Length != result.ByteLength
            || !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(png)), result.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return Error("ANDROID_SCREENSHOT_FAILED", "The Android screenshot payload failed validation.");
        }

        return new CallToolResult
        {
            IsError = false,
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new
                    {
                        result.Width, result.Height, result.MimeType, result.ByteLength, result.Sha256
                    }, JsonOptions.Default)
                },
                ImageContentBlock.FromBytes(png, "image/png")
            ]
        };
    }

    private async Task<bool> AuthorizedAsync(string policy)
    {
        var principal = _httpContextAccessor?.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        return (await _authorizationService.AuthorizeAsync(principal, null, policy)).Succeeded;
    }

    private CancellationToken RequestToken() =>
        _httpContextAccessor?.HttpContext?.RequestAborted ?? CancellationToken.None;

    private static bool ValidCoordinate(int value) => value is >= 0 and <= MaximumCoordinate;

    private static bool IsSafeAdbText(string? value) => value is { Length: >= 1 and <= 2000 }
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is ' ' or '.' or ',' or '_' or '@' or ':' or '+' or '-' or '/' or '%');

    private static string NormalizeKeyCode(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.StartsWith("KEYCODE_", StringComparison.Ordinal) ? normalized[8..] : normalized;
    }

    private static CallToolResult Json(object value) => new()
    {
        IsError = false,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, JsonOptions.Default) }]
    };

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }]
    };

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageRegex();

    [GeneratedRegex(@"^\.?[A-Za-z][A-Za-z0-9_.$]*(?:\.[A-Za-z0-9_.$]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ActivityRegex();
}
