using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const int MaxSetValueCharacters = 65_536;
    private const int ValueVerificationAttempts = 10;
    private const int ValueVerificationDelayMilliseconds = 50;

    public async Task<CommandResult<UiGetValueResult>> GetValueAsync(
        string windowHandle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return GetValueFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            return GetValueFailure(commandId, ErrorCodes.InvalidRequest, "automationId or name is required.");
        if (occurrenceIndex is < 0 or > 1000)
            return GetValueFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (!OperatingSystem.IsWindows())
            return GetValueFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI value reading is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => GetValueWindows(
                    handle,
                    automationId?.Trim(),
                    name?.Trim(),
                    controlType?.Trim(),
                    occurrenceIndex,
                    focusWindow,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return GetValueFailure(commandId, ErrorCodes.CommandCancelled, "The UI value read request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation value read failure for command {CommandId}", commandId);
            return GetValueFailure(commandId, ErrorCodes.UiValueReadFailed, "Windows UI Automation could not read the requested control value.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI value read failure for command {CommandId}", commandId);
            return GetValueFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while reading the control value.");
        }
    }

    public async Task<CommandResult<UiSetValueResult>> SetValueAsync(
        string windowHandle,
        string value,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        bool append,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return SetValueFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (value is null)
            return SetValueFailure(commandId, ErrorCodes.InvalidRequest, "value is required. Use an empty string to clear the control.");
        if (value.Length > MaxSetValueCharacters)
            return SetValueFailure(commandId, ErrorCodes.InvalidRequest, $"value must be at most {MaxSetValueCharacters} characters.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            return SetValueFailure(commandId, ErrorCodes.InvalidRequest, "automationId or name is required.");
        if (occurrenceIndex is < 0 or > 1000)
            return SetValueFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (!OperatingSystem.IsWindows())
            return SetValueFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI value writing is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => SetValueWindows(
                    handle,
                    value,
                    automationId?.Trim(),
                    name?.Trim(),
                    controlType?.Trim(),
                    occurrenceIndex,
                    focusWindow,
                    append,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return SetValueFailure(commandId, ErrorCodes.CommandCancelled, "The UI value write request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation value write failure for command {CommandId}", commandId);
            return SetValueFailure(commandId, ErrorCodes.UiValueWriteFailed, "Windows UI Automation could not write the requested control value.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI value write failure for command {CommandId}", commandId);
            return SetValueFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while writing the control value.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiGetValueResult> GetValueWindows(
        IntPtr handle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return GetValueFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        try
        {
            automation = CreateAutomationClient();
            root = automation.ElementFromHandle(handle);
            if (root is null)
                return GetValueFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");

            if (focusWindow)
                root.SetFocus();

            walker = automation.ControlViewWalker;
            var seen = 0;
            var visited = 0;
            match = FindTarget(
                root,
                walker,
                automationId,
                name,
                controlType,
                occurrenceIndex,
                ref seen,
                ref visited,
                cancellationToken);

            if (match is null)
                return GetValueFailure(commandId, ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");

            var isPassword = ReadOrDefault(() => match.CurrentIsPassword != 0, false);
            var value = ReadValue(match, isPassword, out var valueTruncated);
            if (!isPassword && value is null)
                return GetValueFailure(commandId, ErrorCodes.UiValueNotSupported, "The matched control does not expose a readable value pattern.");

            return new CommandResult<UiGetValueResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new UiGetValueResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    Name = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty)),
                    AutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty)),
                    ControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0)),
                    Bounds = ReadBounds(match),
                    Enabled = ReadOrDefault(() => match.CurrentIsEnabled != 0, false),
                    IsPassword = isPassword,
                    ValueSupported = true,
                    Value = isPassword ? null : value,
                    ValueTruncated = !isPassword && valueTruncated,
                    OccurrenceIndex = occurrenceIndex
                }
            };
        }
        finally
        {
            if (!ReferenceEquals(match, root))
                ReleaseComObject(match);
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiSetValueResult> SetValueWindows(
        IntPtr handle,
        string value,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        bool append,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return SetValueFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        try
        {
            automation = CreateAutomationClient();
            root = automation.ElementFromHandle(handle);
            if (root is null)
                return SetValueFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");

            if (focusWindow)
                root.SetFocus();

            walker = automation.ControlViewWalker;
            var seen = 0;
            var visited = 0;
            match = FindTarget(
                root,
                walker,
                automationId,
                name,
                controlType,
                occurrenceIndex,
                ref seen,
                ref visited,
                cancellationToken);

            if (match is null)
                return SetValueFailure(commandId, ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");
            if (ReadOrDefault(() => match.CurrentIsEnabled == 0, true))
                return SetValueFailure(commandId, ErrorCodes.UiValueWriteFailed, "The matched control is disabled.");

            var isPassword = ReadOrDefault(() => match.CurrentIsPassword != 0, false);
            if (append && isPassword)
                return SetValueFailure(commandId, ErrorCodes.InvalidRequest, "append is not supported for password controls because their existing value is intentionally unreadable.");

            var targetValue = value;
            if (append)
            {
                var currentValue = ReadExactWritableValue(match);
                if (currentValue is null)
                    return SetValueFailure(commandId, ErrorCodes.UiValueNotSupported, "The matched control does not expose a readable value required for append.");
                if (currentValue.Length + value.Length > MaxSetValueCharacters)
                    return SetValueFailure(commandId, ErrorCodes.InvalidRequest, $"The appended value would exceed {MaxSetValueCharacters} characters.");
                targetValue = currentValue + value;
            }

            if (focusWindow)
                match.SetFocus();

            var writeMethod = TryWriteValue(match, targetValue, out var readOnly);
            if (writeMethod is null)
            {
                return SetValueFailure(
                    commandId,
                    readOnly ? ErrorCodes.UiValueReadOnly : ErrorCodes.UiValueNotSupported,
                    readOnly
                        ? "The matched control exposes a read-only value."
                        : "The matched control does not expose a writable ValuePattern or LegacyIAccessible value.");
            }

            var verified = false;
            if (!isPassword)
            {
                verified = WaitForValue(match, targetValue, cancellationToken);
                if (!verified)
                    return SetValueFailure(commandId, ErrorCodes.UiValueVerificationFailed, "The control accepted the write request but its value did not match during verification.");
            }

            var valueTruncated = false;
            var resultValue = isPassword ? null : LimitValue(targetValue, out valueTruncated);
            return new CommandResult<UiSetValueResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new UiSetValueResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    Name = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty)),
                    AutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty)),
                    ControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0)),
                    Bounds = ReadBounds(match),
                    WriteMethod = writeMethod,
                    IsPassword = isPassword,
                    Appended = append,
                    Verified = verified,
                    ValueLength = targetValue.Length,
                    Value = resultValue,
                    ValueTruncated = !isPassword && valueTruncated,
                    OccurrenceIndex = occurrenceIndex
                }
            };
        }
        finally
        {
            if (!ReferenceEquals(match, root))
                ReleaseComObject(match);
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IUIAutomation CreateAutomationClient()
    {
        IUIAutomation automation = new CUIAutomation8();
        if (automation is IUIAutomation2 automation2)
        {
            automation2.ConnectionTimeout = 5000;
            automation2.TransactionTimeout = 5000;
        }

        return automation;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadExactWritableValue(IUIAutomationElement element) =>
        TryReadValuePattern(element) ?? TryReadLegacyValuePattern(element);

    [SupportedOSPlatform("windows")]
    private static string? TryWriteValue(
        IUIAutomationElement element,
        string value,
        out bool readOnly)
    {
        readOnly = false;
        object? pattern = null;
        try
        {
            pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId);
            if (pattern is IUIAutomationValuePattern valuePattern)
            {
                if (valuePattern.CurrentIsReadOnly != 0)
                {
                    readOnly = true;
                    return null;
                }

                valuePattern.SetValue(value);
                return "value-pattern";
            }
        }
        catch (COMException)
        {
        }
        finally
        {
            ReleaseComObject(pattern);
        }

        try
        {
            pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_LegacyIAccessiblePatternId);
            if (pattern is IUIAutomationLegacyIAccessiblePattern legacyPattern)
            {
                legacyPattern.SetValue(value);
                return "legacy-value";
            }
        }
        catch (COMException)
        {
        }
        finally
        {
            ReleaseComObject(pattern);
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static bool WaitForValue(
        IUIAutomationElement element,
        string expected,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ValueVerificationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(ReadExactWritableValue(element), expected, StringComparison.Ordinal))
                return true;
            Thread.Sleep(ValueVerificationDelayMilliseconds);
        }

        return string.Equals(ReadExactWritableValue(element), expected, StringComparison.Ordinal);
    }

    private static string LimitValue(string value, out bool truncated)
    {
        truncated = value.Length > MaxValueCharacters;
        return truncated ? value[..MaxValueCharacters] : value;
    }

    private static CommandResult<UiGetValueResult> GetValueFailure(Guid commandId, string code, string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };

    private static CommandResult<UiSetValueResult> SetValueFailure(Guid commandId, string code, string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };
}
