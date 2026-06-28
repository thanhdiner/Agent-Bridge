using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
namespace LocalMcp.Agent.Windows.UiAutomation;
public sealed partial class UiAutomationExecutor
{
    private const int ToggleVerificationAttempts = 10;
    private const int ToggleVerificationDelayMilliseconds = 50;
    public async Task<CommandResult<UiToggleResult>> ToggleAsync(
        string windowHandle,
        string action,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return ToggleFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            return ToggleFailure(commandId, ErrorCodes.InvalidRequest, "automationId or name is required.");
        if (automationId?.Length > 1024 || name?.Length > 1024 || controlType?.Length > 128)
            return ToggleFailure(commandId, ErrorCodes.InvalidRequest, "Selector values exceed their maximum lengths.");
        if (occurrenceIndex is < 0 or > 1000)
            return ToggleFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (!UiToggleActions.TryNormalize(action, out var normalizedAction))
            return ToggleFailure(commandId, ErrorCodes.InvalidRequest, "action must be one of: on, off, toggle.");
        if (!OperatingSystem.IsWindows())
            return ToggleFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI toggle is only available on Windows agents.");
        try
        {
            return await Task.Run(
                () => ToggleWindows(
                    handle,
                    normalizedAction,
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
            return ToggleFailure(commandId, ErrorCodes.CommandCancelled, "The UI toggle request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation toggle failure for command {CommandId}", commandId);
            return ToggleFailure(commandId, ErrorCodes.UiToggleFailed, "Windows UI Automation could not change the requested toggle state.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI toggle failure for command {CommandId}", commandId);
            return ToggleFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while changing toggle state.");
        }
    }
    [SupportedOSPlatform("windows")]
    private static CommandResult<UiToggleResult> ToggleWindows(
        IntPtr handle,
        string action,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return ToggleFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");
        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        object? togglePatternObject = null;
        try
        {
            automation = CreateAutomationClient();
            root = automation.ElementFromHandle(handle);
            if (root is null)
                return ToggleFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");
            if (focusWindow)
                root.SetFocus();
            walker = automation.ControlViewWalker;
            var seen = 0;
            var visited = 0;
            match = FindTarget(root, walker, automationId, name, controlType, occurrenceIndex, ref seen, ref visited, cancellationToken);
            if (match is null)
                return ToggleFailure(commandId, ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");
            if (!ReadOrDefault(() => match.CurrentIsEnabled != 0, false))
                return ToggleFailure(commandId, ErrorCodes.UiToggleFailed, "The matched control is disabled.");
            var matchedName = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty));
            var matchedAutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty));
            var matchedControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0));
            var bounds = ReadBounds(match);
            var scrolledIntoView = TryScrollToggleItemIntoView(match, bounds);
            togglePatternObject = match.GetCurrentPattern(UIA_PatternIds.UIA_TogglePatternId);
            if (togglePatternObject is not IUIAutomationTogglePattern togglePattern)
                return ToggleFailure(commandId, ErrorCodes.UiToggleNotSupported, "The matched control does not expose TogglePattern.");
            if (!TryReadToggleState(togglePattern, out var stateBefore))
                return ToggleFailure(commandId, ErrorCodes.UiToggleFailed, "The toggle state could not be read before the action.");
            if (!ApplyToggleAction(
                    togglePattern,
                    action,
                    stateBefore,
                    cancellationToken,
                    out var method,
                    out var stateAfter))
            {
                return ToggleFailure(
                    commandId,
                    ErrorCodes.UiToggleVerificationFailed,
                    "The toggle state did not match during verification.");
            }
            return new CommandResult<UiToggleResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new UiToggleResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    Name = matchedName,
                    AutomationId = matchedAutomationId,
                    ControlType = matchedControlType,
                    Bounds = ReadBounds(match),
                    Action = action,
                    ToggleMethod = method,
                    StateBefore = MapToggleState(stateBefore),
                    StateAfter = MapToggleState(stateAfter),
                    Verified = true,
                    ScrolledIntoView = scrolledIntoView,
                    OccurrenceIndex = occurrenceIndex
                }
            };
        }
        finally
        {
            ReleaseComObject(togglePatternObject);
            if (!ReferenceEquals(match, root))
                ReleaseComObject(match);
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }
    [SupportedOSPlatform("windows")]
    private static bool TryScrollToggleItemIntoView(IUIAutomationElement element, UiBounds bounds)
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
    [SupportedOSPlatform("windows")]
    private static bool ApplyToggleAction(
        IUIAutomationTogglePattern pattern,
        string action,
        ToggleState stateBefore,
        CancellationToken cancellationToken,
        out string method,
        out ToggleState stateAfter)
    {
        method = "no-op";
        stateAfter = stateBefore;
        if (action == UiToggleActions.Toggle)
        {
            pattern.Toggle();
            method = "toggle";
            return WaitForToggleStateChange(pattern, stateBefore, cancellationToken, out stateAfter);
        }
        var targetState = action == UiToggleActions.On
            ? ToggleState.ToggleState_On
            : ToggleState.ToggleState_Off;
        if (stateBefore == targetState)
            return true;
        var currentState = stateBefore;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            pattern.Toggle();
            method = attempt == 1 ? "toggle" : "toggle-twice";
            if (!WaitForToggleStateChange(pattern, currentState, cancellationToken, out currentState))
            {
                stateAfter = currentState;
                return false;
            }
            if (currentState == targetState)
            {
                stateAfter = currentState;
                return true;
            }
        }
        stateAfter = currentState;
        return false;
    }
    [SupportedOSPlatform("windows")]
    private static bool WaitForToggleStateChange(
        IUIAutomationTogglePattern pattern,
        ToggleState previousState,
        CancellationToken cancellationToken,
        out ToggleState state)
    {
        state = previousState;
        for (var attempt = 0; attempt < ToggleVerificationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadToggleState(pattern, out state) && state != previousState)
                return true;
            Thread.Sleep(ToggleVerificationDelayMilliseconds);
        }
        return TryReadToggleState(pattern, out state) && state != previousState;
    }
    [SupportedOSPlatform("windows")]
    private static bool TryReadToggleState(IUIAutomationTogglePattern pattern, out ToggleState state)
    {
        try
        {
            state = pattern.CurrentToggleState;
            return true;
        }
        catch (COMException)
        {
            state = (ToggleState)(-1);
            return false;
        }
    }
    private static string MapToggleState(ToggleState state) =>
        state switch
        {
            ToggleState.ToggleState_Off => UiToggleStates.Off,
            ToggleState.ToggleState_On => UiToggleStates.On,
            ToggleState.ToggleState_Indeterminate => UiToggleStates.Indeterminate,
            _ => UiToggleStates.Unknown
        };
    private static CommandResult<UiToggleResult> ToggleFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };
}
