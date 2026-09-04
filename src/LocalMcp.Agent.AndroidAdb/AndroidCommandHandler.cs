using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using Microsoft.Extensions.Options;

namespace LocalMcp.Agent.AndroidAdb;

public sealed partial class AndroidCommandHandler
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly HashSet<string> AllowedKeyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BACK", "HOME", "APP_SWITCH", "ENTER", "ESCAPE", "TAB", "SPACE", "DEL", "FORWARD_DEL",
        "DPAD_UP", "DPAD_DOWN", "DPAD_LEFT", "DPAD_RIGHT", "DPAD_CENTER", "MOVE_HOME", "MOVE_END",
        "PAGE_UP", "PAGE_DOWN", "VOLUME_UP", "VOLUME_DOWN", "VOLUME_MUTE", "MEDIA_PLAY_PAUSE",
        "MEDIA_NEXT", "MEDIA_PREVIOUS"
    };

    private readonly IAdbExecutor _adb;
    private readonly AndroidAdbOptions _options;
    private readonly ILogger<AndroidCommandHandler> _logger;

    public AndroidCommandHandler(
        IAdbExecutor adb,
        IOptions<AndroidAdbOptions> options,
        ILogger<AndroidCommandHandler> logger)
    {
        _adb = adb;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AndroidAgentIdentity> ProbeAsync(CancellationToken cancellationToken)
    {
        var state = await RunRequiredTextAsync(["get-state"], 1024, cancellationToken);
        if (!string.Equals(state, "device", StringComparison.OrdinalIgnoreCase))
            throw new AdbCommandException(ErrorCodes.AndroidDeviceUnavailable, $"ADB device '{_options.Serial}' is in state '{state}'.");

        var manufacturer = await GetPropertyAsync("ro.product.manufacturer", cancellationToken);
        var model = await GetPropertyAsync("ro.product.model", cancellationToken);
        var displayName = string.IsNullOrWhiteSpace(_options.DisplayName)
            ? string.Join(' ', new[] { manufacturer, model }.Where(value => !string.IsNullOrWhiteSpace(value)))
            : _options.DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = $"Android {_options.Serial}";

        var deviceId = string.IsNullOrWhiteSpace(_options.DeviceId)
            ? BuildDeviceId(_options.Serial)
            : _options.DeviceId.Trim();
        return new AndroidAgentIdentity(deviceId, displayName, _options.Serial);
    }

    public async Task<CommandResult<JsonElement>> HandleAsync(
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            object data = command switch
            {
                AndroidGetStateCommand value => await GetStateAsync(value, cancellationToken),
                AndroidScreenshotCommand value => await ScreenshotAsync(value, cancellationToken),
                AndroidUiTreeCommand value => await UiTreeAsync(value, cancellationToken),
                AndroidTapCommand value => await TapAsync(value, cancellationToken),
                AndroidSwipeCommand value => await SwipeAsync(value, cancellationToken),
                AndroidTypeTextCommand value => await TypeTextAsync(value, cancellationToken),
                AndroidPressKeyCommand value => await PressKeyAsync(value, cancellationToken),
                AndroidOpenAppCommand value => await OpenAppAsync(value, cancellationToken),
                _ => throw new AdbCommandException(ErrorCodes.UnsupportedCommand, $"Command type '{command.GetType().Name}' is not supported by the Android ADB agent.")
            };

            return Success(command.CommandId, data);
        }
        catch (AdbUnavailableException ex)
        {
            _logger.LogError(ex, "ADB is unavailable while handling command {CommandId}", command.CommandId);
            return Failure(command.CommandId, ErrorCodes.AndroidAdbNotFound, ex.Message);
        }
        catch (AdbCommandException ex)
        {
            _logger.LogWarning(ex, "Android command {CommandId} failed with {ErrorCode}", command.CommandId, ex.ErrorCode);
            return Failure(command.CommandId, ex.ErrorCode, ex.Message);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Android command {CommandId} timed out", command.CommandId);
            return Failure(command.CommandId, ErrorCodes.CommandTimeout, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(command.CommandId, ErrorCodes.CommandCancelled, "Android command was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Android command failure for {CommandId}", command.CommandId);
            return Failure(command.CommandId, ErrorCodes.InternalError, "Android agent failed to execute the command.");
        }
    }

    private async Task<AndroidDeviceStateResult> GetStateAsync(
        AndroidGetStateCommand command,
        CancellationToken cancellationToken)
    {
        _ = command;
        var state = await RunRequiredTextAsync(["get-state"], 1024, cancellationToken);
        var focus = await RunRequiredTextAsync(["shell", "dumpsys", "window", "windows"], 2 * 1024 * 1024, cancellationToken);
        var component = CurrentFocusRegex().Match(focus).Groups["component"].Value;
        var slashIndex = component.IndexOf('/');

        return new AndroidDeviceStateResult
        {
            Serial = _options.Serial,
            State = state,
            Manufacturer = await GetPropertyAsync("ro.product.manufacturer", cancellationToken),
            Model = await GetPropertyAsync("ro.product.model", cancellationToken),
            AndroidVersion = await GetPropertyAsync("ro.build.version.release", cancellationToken),
            SdkVersion = await GetPropertyAsync("ro.build.version.sdk", cancellationToken),
            ScreenSize = await GetScreenSizeAsync(cancellationToken),
            CurrentPackage = slashIndex > 0 ? component[..slashIndex] : string.Empty,
            CurrentActivity = slashIndex > 0 && slashIndex < component.Length - 1 ? component[(slashIndex + 1)..] : string.Empty
        };
    }

    private async Task<AndroidScreenshotResult> ScreenshotAsync(
        AndroidScreenshotCommand command,
        CancellationToken cancellationToken)
    {
        _ = command;
        var result = await _adb.ExecuteAsync(
            ["exec-out", "screencap", "-p"],
            _options.MaxScreenshotBytes,
            cancellationToken);
        EnsureSuccess(result, ErrorCodes.AndroidScreenshotFailed, "capture Android screenshot");
        if (result.OutputLimitExceeded)
            throw new AdbCommandException(ErrorCodes.AndroidScreenshotTooLarge, $"Screenshot exceeds {_options.MaxScreenshotBytes} bytes.");

        var png = result.StandardOutput;
        if (png.Length < 24 || !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            throw new AdbCommandException(ErrorCodes.AndroidScreenshotFailed, "ADB returned an invalid PNG screenshot.");

        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        if (width < 1 || height < 1)
            throw new AdbCommandException(ErrorCodes.AndroidScreenshotFailed, "ADB returned invalid screenshot dimensions.");

        return new AndroidScreenshotResult
        {
            Width = width,
            Height = height,
            ByteLength = png.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(),
            PngBase64 = Convert.ToBase64String(png)
        };
    }

    private async Task<AndroidUiTreeResult> UiTreeAsync(
        AndroidUiTreeCommand command,
        CancellationToken cancellationToken)
    {
        if (command.MaxCharacters is < 1_000 or > 500_000)
            throw new AdbCommandException(ErrorCodes.InvalidRequest, "MaxCharacters must be between 1000 and 500000.");

        var result = await _adb.ExecuteAsync(
            ["exec-out", "uiautomator", "dump", "/dev/tty"],
            Math.Min(command.MaxCharacters * 4 + 4096, 4 * 1024 * 1024),
            cancellationToken);
        EnsureSuccess(result, ErrorCodes.AndroidUiTreeFailed, "dump Android UI hierarchy");
        var text = result.StandardOutputText;
        var xmlStart = text.IndexOf("<?xml", StringComparison.Ordinal);
        var xmlEnd = text.LastIndexOf("</hierarchy>", StringComparison.Ordinal);
        if (xmlStart < 0 || xmlEnd < xmlStart)
            throw new AdbCommandException(ErrorCodes.AndroidUiTreeFailed, "ADB did not return a UI hierarchy XML document.");

        var xml = text[xmlStart..(xmlEnd + "</hierarchy>".Length)];
        var characterCount = xml.Length;
        var truncated = characterCount > command.MaxCharacters;
        if (truncated)
            xml = xml[..command.MaxCharacters];
        return new AndroidUiTreeResult { Xml = xml, CharacterCount = characterCount, Truncated = truncated };
    }

    private async Task<AndroidInputResult> TapAsync(AndroidTapCommand command, CancellationToken cancellationToken)
    {
        if (!ValidCoordinate(command.X) || !ValidCoordinate(command.Y))
            throw new AdbCommandException(ErrorCodes.InvalidRequest, "Tap coordinates must be between 0 and 100000.");
        await RunInputAsync(["shell", "input", "tap", command.X.ToString(), command.Y.ToString()], "tap", cancellationToken);
        return new AndroidInputResult { Action = "tap", Applied = true };
    }

    private async Task<AndroidInputResult> SwipeAsync(AndroidSwipeCommand command, CancellationToken cancellationToken)
    {
        if (!new[] { command.StartX, command.StartY, command.EndX, command.EndY }.All(ValidCoordinate)
            || command.DurationMs is < 0 or > 10_000)
        {
            throw new AdbCommandException(ErrorCodes.InvalidRequest, "Swipe coordinates or duration are invalid.");
        }

        await RunInputAsync(
            ["shell", "input", "swipe", command.StartX.ToString(), command.StartY.ToString(), command.EndX.ToString(), command.EndY.ToString(), command.DurationMs.ToString()],
            "swipe",
            cancellationToken);
        return new AndroidInputResult { Action = "swipe", Applied = true };
    }

    private async Task<AndroidInputResult> TypeTextAsync(AndroidTypeTextCommand command, CancellationToken cancellationToken)
    {
        if (!IsSafeAdbText(command.Text))
            throw new AdbCommandException(ErrorCodes.InvalidRequest, "Text contains characters unsupported by the safe ADB text-input mode. Use printable ASCII letters, digits, spaces, and .,_@:+-/ only.");

        var encoded = command.Text.Replace("%", "%25", StringComparison.Ordinal).Replace(" ", "%s", StringComparison.Ordinal);
        await RunInputAsync(["shell", "input", "text", encoded], "type text", cancellationToken);
        return new AndroidInputResult { Action = "type_text", Applied = true };
    }

    private async Task<AndroidInputResult> PressKeyAsync(AndroidPressKeyCommand command, CancellationToken cancellationToken)
    {
        var keyCode = NormalizeKeyCode(command.KeyCode);
        if (!AllowedKeyCodes.Contains(keyCode))
            throw new AdbCommandException(ErrorCodes.InvalidRequest, $"Key code '{command.KeyCode}' is not allowed.");

        await RunInputAsync(["shell", "input", "keyevent", $"KEYCODE_{keyCode}"], "press key", cancellationToken);
        return new AndroidInputResult { Action = "press_key", Applied = true };
    }

    private async Task<AndroidOpenAppResult> OpenAppAsync(AndroidOpenAppCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.PackageName) || !PackageRegex().IsMatch(command.PackageName))
            throw new AdbCommandException(ErrorCodes.InvalidRequest, "packageName is invalid.");

        IReadOnlyList<string> arguments;
        if (string.IsNullOrWhiteSpace(command.Activity))
        {
            arguments = ["shell", "monkey", "-p", command.PackageName, "-c", "android.intent.category.LAUNCHER", "1"];
        }
        else
        {
            if (!ActivityRegex().IsMatch(command.Activity))
                throw new AdbCommandException(ErrorCodes.InvalidRequest, "activity is invalid.");
            var component = $"{command.PackageName}/{command.Activity}";
            arguments = ["shell", "am", "start", "-n", component];
        }

        var result = await _adb.ExecuteAsync(arguments, 64 * 1024, cancellationToken);
        EnsureSuccess(result, ErrorCodes.AndroidAppLaunchFailed, $"start package '{command.PackageName}'");
        return new AndroidOpenAppResult { PackageName = command.PackageName, Activity = command.Activity, Started = true };
    }

    private async Task RunInputAsync(IReadOnlyList<string> arguments, string operation, CancellationToken cancellationToken)
    {
        var result = await _adb.ExecuteAsync(arguments, 64 * 1024, cancellationToken);
        EnsureSuccess(result, ErrorCodes.AndroidInputFailed, operation);
    }

    private async Task<string> GetPropertyAsync(string property, CancellationToken cancellationToken) =>
        await RunRequiredTextAsync(["shell", "getprop", property], 4096, cancellationToken);

    private async Task<string> GetScreenSizeAsync(CancellationToken cancellationToken)
    {
        var output = await RunRequiredTextAsync(["shell", "wm", "size"], 4096, cancellationToken);
        var matches = ScreenSizeRegex().Matches(output);
        return matches.Count == 0 ? string.Empty : matches[^1].Groups["size"].Value;
    }

    private async Task<string> RunRequiredTextAsync(
        IReadOnlyList<string> arguments,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        var result = await _adb.ExecuteAsync(arguments, maxOutputBytes, cancellationToken);
        EnsureSuccess(result, ErrorCodes.AndroidAdbFailed, string.Join(' ', arguments));
        if (result.OutputLimitExceeded)
            throw new AdbCommandException(ErrorCodes.AndroidAdbFailed, "ADB output exceeded the configured safety limit.");
        return result.StandardOutputText;
    }

    private static void EnsureSuccess(AdbExecutionResult result, string errorCode, string operation)
    {
        if (result.ExitCode == 0)
            return;
        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutputText : result.StandardError;
        if (detail.Length > 512)
            detail = detail[..512];
        throw new AdbCommandException(errorCode, $"ADB failed to {operation} (exit {result.ExitCode}): {detail}");
    }

    private static CommandResult<JsonElement> Success(Guid commandId, object data) => new()
    {
        CommandId = commandId,
        Success = true,
        Data = JsonSerializer.SerializeToElement(data, JsonOptions.Default)
    };

    private static CommandResult<JsonElement> Failure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message),
        Data = JsonSerializer.SerializeToElement<object?>(null)
    };

    internal static string BuildDeviceId(string serial)
    {
        var builder = new StringBuilder("android-");
        foreach (var character in serial.Trim().ToLowerInvariant())
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-');
        return builder.ToString();
    }

    internal static string NormalizeKeyCode(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.StartsWith("KEYCODE_", StringComparison.Ordinal) ? normalized[8..] : normalized;
    }

    internal static bool IsSafeAdbText(string? value) =>
        value is { Length: >= 1 and <= 2000 }
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is ' ' or '.' or ',' or '_' or '@' or ':' or '+' or '-' or '/' or '%');

    private static bool ValidCoordinate(int value) => value is >= 0 and <= 100_000;

    [GeneratedRegex(@"mCurrentFocus=.*?\s(?<component>[A-Za-z0-9._]+/[A-Za-z0-9._$]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CurrentFocusRegex();

    [GeneratedRegex(@"(?<size>\d+x\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ScreenSizeRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageRegex();

    [GeneratedRegex(@"^\.?[A-Za-z][A-Za-z0-9_.$]*(?:\.[A-Za-z0-9_.$]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ActivityRegex();
}

public sealed record AndroidAgentIdentity(string DeviceId, string DisplayName, string Serial);

public sealed class AdbCommandException : Exception
{
    public AdbCommandException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
