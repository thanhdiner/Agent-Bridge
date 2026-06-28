using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    public async Task<CommandResult<UiGetStateResult>> GetStateAsync(
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
            return GetStateFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            return GetStateFailure(commandId, ErrorCodes.InvalidRequest, "automationId or name is required.");
        if (automationId?.Length > 1024 || name?.Length > 1024 || controlType?.Length > 128)
            return GetStateFailure(commandId, ErrorCodes.InvalidRequest, "Selector values exceed their maximum lengths.");
        if (occurrenceIndex is < 0 or > 1000)
            return GetStateFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (!OperatingSystem.IsWindows())
            return GetStateFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI state reading is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => GetStateWindows(
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
            return GetStateFailure(commandId, ErrorCodes.CommandCancelled, "The UI state read request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation state read failure for command {CommandId}", commandId);
            return GetStateFailure(commandId, ErrorCodes.UiStateReadFailed, "Windows UI Automation could not read the requested control state.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI state read failure for command {CommandId}", commandId);
            return GetStateFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while reading the control state.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiGetStateResult> GetStateWindows(
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
            return GetStateFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        try
        {
            automation = CreateAutomationClient();
            root = automation.ElementFromHandle(handle);
            if (root is null)
                return GetStateFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");

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
                return GetStateFailure(commandId, ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");

            var selected = ReadSelectionState(match);
            ReadToggleStateSnapshot(match, out var checkedState, out var checkState);
            ReadExpandCollapseStateSnapshot(match, out var expanded, out var expandCollapseState);

            return new CommandResult<UiGetStateResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new UiGetStateResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    Name = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty)),
                    AutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty)),
                    ControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0)),
                    Bounds = ReadBounds(match),
                    Enabled = ReadOrDefault(() => match.CurrentIsEnabled != 0, false),
                    Focused = ReadOrDefault(() => match.CurrentHasKeyboardFocus != 0, false),
                    Offscreen = ReadOrDefault(() => match.CurrentIsOffscreen != 0, false),
                    KeyboardFocusable = ReadOrDefault(() => match.CurrentIsKeyboardFocusable != 0, false),
                    Selected = selected,
                    Checked = checkedState,
                    CheckState = checkState,
                    Expanded = expanded,
                    ExpandCollapseState = expandCollapseState,
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
    private static bool? ReadSelectionState(IUIAutomationElement element)
    {
        object? patternObject = null;
        try
        {
            patternObject = element.GetCurrentPattern(UIA_PatternIds.UIA_SelectionItemPatternId);
            return patternObject is IUIAutomationSelectionItemPattern pattern
                ? pattern.CurrentIsSelected != 0
                : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(patternObject);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadToggleStateSnapshot(
        IUIAutomationElement element,
        out bool? checkedState,
        out string? checkState)
    {
        checkedState = null;
        checkState = null;
        object? patternObject = null;
        try
        {
            patternObject = element.GetCurrentPattern(UIA_PatternIds.UIA_TogglePatternId);
            if (patternObject is not IUIAutomationTogglePattern pattern || !TryReadToggleState(pattern, out var state))
                return;

            checkState = MapToggleState(state);
            checkedState = state switch
            {
                ToggleState.ToggleState_On => true,
                ToggleState.ToggleState_Off => false,
                _ => null
            };
        }
        catch (COMException)
        {
        }
        finally
        {
            ReleaseComObject(patternObject);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadExpandCollapseStateSnapshot(
        IUIAutomationElement element,
        out bool? expanded,
        out string? expandCollapseState)
    {
        expanded = null;
        expandCollapseState = null;
        object? patternObject = null;
        try
        {
            patternObject = element.GetCurrentPattern(UIA_PatternIds.UIA_ExpandCollapsePatternId);
            if (patternObject is not IUIAutomationExpandCollapsePattern pattern
                || !TryReadExpandCollapseState(pattern, out var state))
            {
                return;
            }

            expandCollapseState = MapExpandCollapseState(state);
            expanded = state switch
            {
                ExpandCollapseState.ExpandCollapseState_Expanded => true,
                ExpandCollapseState.ExpandCollapseState_Collapsed => false,
                _ => null
            };
        }
        catch (COMException)
        {
        }
        finally
        {
            ReleaseComObject(patternObject);
        }
    }

    private static CommandResult<UiGetStateResult> GetStateFailure(Guid commandId, string code, string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };
}
