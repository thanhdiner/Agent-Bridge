using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
namespace LocalMcp.Agent.Windows.UiAutomation;
public sealed partial class UiAutomationExecutor
{
    private const int RangeValueVerificationAttempts = 10;
    private const int RangeValueVerificationDelayMilliseconds = 50;
    public async Task<CommandResult<UiRangeValueResult>> RangeValueAsync(
        string windowHandle,
        string action,
        double? value,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return RangeValueFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            return RangeValueFailure(commandId, ErrorCodes.InvalidRequest, "automationId or name is required.");
        if (automationId?.Length > 1024 || name?.Length > 1024 || controlType?.Length > 128)
            return RangeValueFailure(commandId, ErrorCodes.InvalidRequest, "Selector values exceed their maximum lengths.");
        if (occurrenceIndex is < 0 or > 1000)
            return RangeValueFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (!UiRangeValueActions.TryNormalize(action, out var normalizedAction))
            return RangeValueFailure(commandId, ErrorCodes.InvalidRequest, "action must be one of: get, set, increase, decrease.");
        if (normalizedAction == UiRangeValueActions.Set && value is null)
            return RangeValueFailure(commandId, ErrorCodes.InvalidRequest, "value is required when action is set.");
        if (value is not null && !double.IsFinite(value.Value))
            return RangeValueFailure(commandId, ErrorCodes.InvalidRequest, "value must be a finite number.");
        if (normalizedAction != UiRangeValueActions.Set && value is not null)
            return RangeValueFailure(commandId, ErrorCodes.InvalidRequest, "value is only supported when action is set.");
        if (!OperatingSystem.IsWindows())
            return RangeValueFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI range value automation is only available on Windows agents.");
        try
        {
            return await Task.Run(
                () => RangeValueWindows(
                    handle,
                    normalizedAction,
                    value,
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
            return RangeValueFailure(commandId, ErrorCodes.CommandCancelled, "The UI range value request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation range value failure for command {CommandId}", commandId);
            return RangeValueFailure(commandId, ErrorCodes.UiRangeValueFailed, "Windows UI Automation could not complete the requested range value action.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI range value failure for command {CommandId}", commandId);
            return RangeValueFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while changing the range value.");
        }
    }
    [SupportedOSPlatform("windows")]
    private static CommandResult<UiRangeValueResult> RangeValueWindows(
        IntPtr handle,
        string action,
        double? requestedValue,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return RangeValueFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");
        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        object? rangePatternObject = null;
        try
        {
            automation = CreateAutomationClient();
            root = automation.ElementFromHandle(handle);
            if (root is null)
                return RangeValueFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");
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
                return RangeValueFailure(commandId, ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");
            var matchedName = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty));
            var matchedAutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty));
            var matchedControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0));
            var bounds = ReadBounds(match);
            var scrolledIntoView = TryScrollRangeValueIntoView(match, bounds);
            rangePatternObject = match.GetCurrentPattern(UIA_PatternIds.UIA_RangeValuePatternId);
            if (rangePatternObject is not IUIAutomationRangeValuePattern rangePattern)
                return RangeValueFailure(commandId, ErrorCodes.UiRangeValueNotSupported, "The matched control does not expose RangeValuePattern.");
            if (!TryReadRangeValueSnapshot(rangePattern, out var snapshot))
                return RangeValueFailure(commandId, ErrorCodes.UiRangeValueFailed, "The control range metadata could not be read.");
            if (snapshot.Minimum > snapshot.Maximum)
                return RangeValueFailure(commandId, ErrorCodes.UiRangeValueFailed, "The control reported an invalid numeric range.");
            var method = "read";
            var valueAfter = snapshot.Value;
            if (action != UiRangeValueActions.Get)
            {
                if (!ReadOrDefault(() => match.CurrentIsEnabled != 0, false))
                    return RangeValueFailure(commandId, ErrorCodes.UiRangeValueFailed, "The matched control is disabled.");
                if (snapshot.IsReadOnly)
                    return RangeValueFailure(commandId, ErrorCodes.UiRangeValueReadOnly, "The matched control exposes a read-only range value.");
                if (!TryResolveRangeTarget(
                        action,
                        requestedValue,
                        snapshot,
                        out var targetValue,
                        out var targetErrorCode,
                        out var targetErrorMessage))
                {
                    return RangeValueFailure(commandId, targetErrorCode!, targetErrorMessage!);
                }
                if (AreRangeValuesEquivalent(snapshot.Value, targetValue))
                {
                    method = "no-op";
                    valueAfter = snapshot.Value;
                }
                else
                {
                    if (focusWindow)
                        match.SetFocus();
                    rangePattern.SetValue(targetValue);
                    method = "set-value";
                    if (!WaitForRangeValue(rangePattern, targetValue, cancellationToken, out valueAfter))
                    {
                        return RangeValueFailure(
                            commandId,
                            ErrorCodes.UiRangeValueVerificationFailed,
                            "The control accepted the range value request but its value did not match during verification.");
                    }
                }
            }
            return new CommandResult<UiRangeValueResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new UiRangeValueResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    Name = matchedName,
                    AutomationId = matchedAutomationId,
                    ControlType = matchedControlType,
                    Bounds = ReadBounds(match),
                    Action = action,
                    Method = method,
                    ValueBefore = snapshot.Value,
                    ValueAfter = valueAfter,
                    Minimum = snapshot.Minimum,
                    Maximum = snapshot.Maximum,
                    SmallChange = snapshot.SmallChange,
                    LargeChange = snapshot.LargeChange,
                    IsReadOnly = snapshot.IsReadOnly,
                    Verified = true,
                    ScrolledIntoView = scrolledIntoView,
                    OccurrenceIndex = occurrenceIndex
                }
            };
        }
        finally
        {
            ReleaseComObject(rangePatternObject);
            if (!ReferenceEquals(match, root))
                ReleaseComObject(match);
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }
    [SupportedOSPlatform("windows")]
    private static bool TryScrollRangeValueIntoView(IUIAutomationElement element, UiBounds bounds)
    {
        var isOffscreen = ReadOrDefault(() => element.CurrentIsOffscreen != 0, false)
            || bounds.Width <= 0
            || bounds.Height <= 0;
        if (!isOffscreen)
            return false;
        object? patternObject = null;
        try
        {
            patternObject = element.GetCurrentPattern(UIA_PatternIds.UIA_ScrollItemPatternId);
            if (patternObject is not IUIAutomationScrollItemPattern scrollItemPattern)
                return false;
            scrollItemPattern.ScrollIntoView();
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(patternObject);
        }
    }
    internal static bool AreRangeValuesEquivalent(double actual, double expected)
    {
        if (!double.IsFinite(actual) || !double.IsFinite(expected))
            return false;
        var scale = Math.Max(1d, Math.Max(Math.Abs(actual), Math.Abs(expected)));
        return Math.Abs(actual - expected) <= scale * 1e-9;
    }
    private static bool TryResolveRangeTarget(
        string action,
        double? requestedValue,
        RangeValueSnapshot snapshot,
        out double targetValue,
        out string? errorCode,
        out string? errorMessage)
    {
        targetValue = snapshot.Value;
        errorCode = null;
        errorMessage = null;
        if (action == UiRangeValueActions.Set)
        {
            var value = requestedValue!.Value;
            if ((value < snapshot.Minimum && !AreRangeValuesEquivalent(value, snapshot.Minimum))
                || (value > snapshot.Maximum && !AreRangeValuesEquivalent(value, snapshot.Maximum)))
            {
                errorCode = ErrorCodes.UiRangeValueOutOfRange;
                errorMessage = $"value must be between {snapshot.Minimum:G17} and {snapshot.Maximum:G17}.";
                return false;
            }
            targetValue = Math.Clamp(value, snapshot.Minimum, snapshot.Maximum);
            return true;
        }
        if (!double.IsFinite(snapshot.SmallChange) || snapshot.SmallChange <= 0)
        {
            errorCode = ErrorCodes.UiRangeValueFailed;
            errorMessage = "The control does not expose a positive smallChange for increase or decrease.";
            return false;
        }
        targetValue = action == UiRangeValueActions.Increase
            ? Math.Min(snapshot.Maximum, snapshot.Value + snapshot.SmallChange)
            : Math.Max(snapshot.Minimum, snapshot.Value - snapshot.SmallChange);
        return true;
    }
    [SupportedOSPlatform("windows")]
    private static bool WaitForRangeValue(
        IUIAutomationRangeValuePattern pattern,
        double expected,
        CancellationToken cancellationToken,
        out double actual)
    {
        actual = double.NaN;
        for (var attempt = 0; attempt < RangeValueVerificationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadRangeValue(pattern, out actual) && AreRangeValuesEquivalent(actual, expected))
                return true;
            Thread.Sleep(RangeValueVerificationDelayMilliseconds);
        }
        return TryReadRangeValue(pattern, out actual) && AreRangeValuesEquivalent(actual, expected);
    }
    [SupportedOSPlatform("windows")]
    private static bool TryReadRangeValueSnapshot(
        IUIAutomationRangeValuePattern pattern,
        out RangeValueSnapshot snapshot)
    {
        snapshot = default;
        try
        {
            var value = pattern.CurrentValue;
            var minimum = pattern.CurrentMinimum;
            var maximum = pattern.CurrentMaximum;
            var smallChange = pattern.CurrentSmallChange;
            var largeChange = pattern.CurrentLargeChange;
            if (!double.IsFinite(value)
                || !double.IsFinite(minimum)
                || !double.IsFinite(maximum)
                || !double.IsFinite(smallChange)
                || !double.IsFinite(largeChange))
            {
                return false;
            }
            snapshot = new RangeValueSnapshot(
                value,
                minimum,
                maximum,
                smallChange,
                largeChange,
                pattern.CurrentIsReadOnly != 0);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }
    [SupportedOSPlatform("windows")]
    private static bool TryReadRangeValue(IUIAutomationRangeValuePattern pattern, out double value)
    {
        try
        {
            value = pattern.CurrentValue;
            return double.IsFinite(value);
        }
        catch (COMException)
        {
            value = double.NaN;
            return false;
        }
    }
    private readonly record struct RangeValueSnapshot(
        double Value,
        double Minimum,
        double Maximum,
        double SmallChange,
        double LargeChange,
        bool IsReadOnly);
    private static CommandResult<UiRangeValueResult> RangeValueFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };
}
