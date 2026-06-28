using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    public async Task<CommandResult<UiFocusResult>> FocusControlAsync(
        string windowHandle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return FocusControlFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            return FocusControlFailure(commandId, ErrorCodes.InvalidRequest, "automationId or name is required.");
        if (automationId?.Length > 1024 || name?.Length > 1024 || controlType?.Length > 128)
            return FocusControlFailure(commandId, ErrorCodes.InvalidRequest, "Selector values exceed their maximum lengths.");
        if (occurrenceIndex is < 0 or > 1000)
            return FocusControlFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (!OperatingSystem.IsWindows())
            return FocusControlFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI control focus is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => FocusControlWindows(
                    handle,
                    automationId?.Trim(),
                    name?.Trim(),
                    controlType?.Trim(),
                    occurrenceIndex,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return FocusControlFailure(commandId, ErrorCodes.CommandCancelled, "The UI focus request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation control focus failure for command {CommandId}", commandId);
            return FocusControlFailure(commandId, ErrorCodes.UiFocusFailed, "Windows UI Automation could not focus the requested control.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI control focus failure for command {CommandId}", commandId);
            return FocusControlFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while focusing the control.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiFocusResult> FocusControlWindows(
        IntPtr handle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return FocusControlFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            automation = CreateAutomationClient();
            root = automation.ElementFromHandle(handle);
            if (root is null)
                return FocusControlFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");

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
                return FocusControlFailure(commandId, ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");

            var enabled = ReadOrDefault(() => match.CurrentIsEnabled != 0, false);
            if (!enabled)
                return FocusControlFailure(commandId, ErrorCodes.UiFocusFailed, "The matched control is disabled.");

            var keyboardFocusable = ReadOrDefault(() => match.CurrentIsKeyboardFocusable != 0, false);
            var focusedBefore = ReadOrDefault(() => match.CurrentHasKeyboardFocus != 0, false);
            var wasMinimized = IsIconic(handle);
            var restored = false;
            if (wasMinimized)
            {
                ShowWindowAsync(handle, ShowWindowRestore);
                restored = WaitForWindowState(() => !IsIconic(handle), cancellationToken);
                if (!restored)
                    return FocusControlFailure(commandId, ErrorCodes.UiFocusFailed, "The requested window could not be restored from its minimized state.");
            }

            if (GetForegroundWindow() != handle)
            {
                RequestForegroundActivation(handle);
                root.SetFocus();
                if (!WaitForWindowState(() => GetForegroundWindow() == handle, cancellationToken))
                {
                    SwitchToThisWindow(handle, true);
                    RequestForegroundActivation(handle);
                    root.SetFocus();
                    if (!WaitForWindowState(() => GetForegroundWindow() == handle, cancellationToken))
                        return FocusControlFailure(commandId, ErrorCodes.UiFocusFailed, "Windows did not grant foreground activation to the requested window.");
                }
            }

            var scrolledIntoView = TryScrollFocusTargetIntoView(match);
            var currentlyFocused = ReadOrDefault(() => match.CurrentHasKeyboardFocus != 0, false);
            var focusMethod = currentlyFocused ? "no-op" : "set-focus";
            if (!currentlyFocused)
            {
                if (!keyboardFocusable)
                    return FocusControlFailure(commandId, ErrorCodes.UiFocusNotSupported, "The matched control does not report itself as keyboard focusable.");

                match.SetFocus();
            }

            var verified = WaitForControlFocus(match, handle, cancellationToken);
            var focusedAfter = ReadOrDefault(() => match.CurrentHasKeyboardFocus != 0, false);
            if (!verified || !focusedAfter)
                return FocusControlFailure(commandId, ErrorCodes.UiFocusVerificationFailed, "The matched control did not hold keyboard focus during verification.");

            return new CommandResult<UiFocusResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new UiFocusResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    Name = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty)),
                    AutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty)),
                    ControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0)),
                    Bounds = ReadBounds(match),
                    Enabled = enabled,
                    KeyboardFocusable = keyboardFocusable,
                    FocusedBefore = focusedBefore,
                    FocusedAfter = focusedAfter,
                    FocusMethod = focusMethod,
                    Verified = true,
                    ScrolledIntoView = scrolledIntoView,
                    WasMinimized = wasMinimized,
                    Restored = restored,
                    IsForeground = GetForegroundWindow() == handle,
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
    private static bool TryScrollFocusTargetIntoView(IUIAutomationElement element)
    {
        var bounds = ReadBounds(element);
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
    private static bool WaitForControlFocus(
        IUIAutomationElement element,
        IntPtr handle,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < FocusVerificationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() == handle
                && ReadOrDefault(() => element.CurrentHasKeyboardFocus != 0, false))
            {
                return true;
            }

            Thread.Sleep(FocusVerificationDelayMilliseconds);
        }

        return GetForegroundWindow() == handle
            && ReadOrDefault(() => element.CurrentHasKeyboardFocus != 0, false);
    }

    private static CommandResult<UiFocusResult> FocusControlFailure(Guid commandId, string code, string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };
}
