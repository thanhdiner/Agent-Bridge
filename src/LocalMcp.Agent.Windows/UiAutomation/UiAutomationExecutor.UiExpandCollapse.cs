using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const int ExpandCollapseVerificationAttempts = 10;
    private const int ExpandCollapseVerificationDelayMilliseconds = 50;

    public async Task<CommandResult<UiExpandCollapseResult>> ExpandCollapseAsync(
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
            return ExpandCollapseFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            return ExpandCollapseFailure(commandId, ErrorCodes.InvalidRequest, "automationId or name is required.");
        if (automationId?.Length > 1024 || name?.Length > 1024 || controlType?.Length > 128)
            return ExpandCollapseFailure(commandId, ErrorCodes.InvalidRequest, "Selector values exceed their maximum lengths.");
        if (occurrenceIndex is < 0 or > 1000)
            return ExpandCollapseFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (!UiExpandCollapseActions.TryNormalize(action, out var normalizedAction))
            return ExpandCollapseFailure(commandId, ErrorCodes.InvalidRequest, "action must be one of: expand, collapse, toggle.");
        if (!OperatingSystem.IsWindows())
            return ExpandCollapseFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI expand/collapse is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => ExpandCollapseWindows(
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
            return ExpandCollapseFailure(commandId, ErrorCodes.CommandCancelled, "The UI expand/collapse request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation expand/collapse failure for command {CommandId}", commandId);
            return ExpandCollapseFailure(commandId, ErrorCodes.UiExpandCollapseFailed, "Windows UI Automation could not change the requested expand/collapse state.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI expand/collapse failure for command {CommandId}", commandId);
            return ExpandCollapseFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while changing expand/collapse state.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiExpandCollapseResult> ExpandCollapseWindows(
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
            return ExpandCollapseFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        object? expandCollapsePatternObject = null;
        try
        {
            automation = CreateAutomationClient();
            root = automation.ElementFromHandle(handle);
            if (root is null)
                return ExpandCollapseFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");

            if (focusWindow)
                root.SetFocus();

            walker = automation.ControlViewWalker;
            var seen = 0;
            var visited = 0;
            match = FindTarget(root, walker, automationId, name, controlType, occurrenceIndex, ref seen, ref visited, cancellationToken);
            if (match is null)
                return ExpandCollapseFailure(commandId, ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");
            if (!ReadOrDefault(() => match.CurrentIsEnabled != 0, false))
                return ExpandCollapseFailure(commandId, ErrorCodes.UiExpandCollapseFailed, "The matched control is disabled.");

            var matchedName = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty));
            var matchedAutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty));
            var matchedControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0));
            var bounds = ReadBounds(match);
            var scrolledIntoView = TryScrollExpandCollapseItemIntoView(match, bounds);

            expandCollapsePatternObject = match.GetCurrentPattern(UIA_PatternIds.UIA_ExpandCollapsePatternId);
            if (expandCollapsePatternObject is not IUIAutomationExpandCollapsePattern expandCollapsePattern)
                return ExpandCollapseFailure(commandId, ErrorCodes.UiExpandCollapseNotSupported, "The matched control does not expose ExpandCollapsePattern.");

            if (!TryReadExpandCollapseState(expandCollapsePattern, out var stateBefore))
                return ExpandCollapseFailure(commandId, ErrorCodes.UiExpandCollapseFailed, "The expand/collapse state could not be read before the action.");
            if (stateBefore == ExpandCollapseState.ExpandCollapseState_LeafNode)
                return ExpandCollapseFailure(commandId, ErrorCodes.UiExpandCollapseNotSupported, "The matched control is a leaf node and cannot be expanded or collapsed.");

            var expectedState = ResolveExpectedExpandCollapseState(action, stateBefore);
            var method = "no-op";
            if (stateBefore != expectedState)
                method = ApplyExpandCollapseAction(expandCollapsePattern, expectedState);

            var verified = WaitForExpandCollapseState(expandCollapsePattern, expectedState, cancellationToken, out var stateAfter);
            if (!verified)
                return ExpandCollapseFailure(commandId, ErrorCodes.UiExpandCollapseVerificationFailed, "The expand/collapse state did not match during verification.");

            return new CommandResult<UiExpandCollapseResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new UiExpandCollapseResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    Name = matchedName,
                    AutomationId = matchedAutomationId,
                    ControlType = matchedControlType,
                    Bounds = ReadBounds(match),
                    Action = action,
                    ExpandCollapseMethod = method,
                    StateBefore = MapExpandCollapseState(stateBefore),
                    StateAfter = MapExpandCollapseState(stateAfter),
                    Verified = true,
                    ScrolledIntoView = scrolledIntoView,
                    OccurrenceIndex = occurrenceIndex
                }
            };
        }
        finally
        {
            ReleaseComObject(expandCollapsePatternObject);
            if (!ReferenceEquals(match, root))
                ReleaseComObject(match);
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryScrollExpandCollapseItemIntoView(IUIAutomationElement element, UiBounds bounds)
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

    private static ExpandCollapseState ResolveExpectedExpandCollapseState(string action, ExpandCollapseState currentState) =>
        action switch
        {
            UiExpandCollapseActions.Expand => ExpandCollapseState.ExpandCollapseState_Expanded,
            UiExpandCollapseActions.Collapse => ExpandCollapseState.ExpandCollapseState_Collapsed,
            UiExpandCollapseActions.Toggle when currentState is ExpandCollapseState.ExpandCollapseState_Expanded
                or ExpandCollapseState.ExpandCollapseState_PartiallyExpanded => ExpandCollapseState.ExpandCollapseState_Collapsed,
            UiExpandCollapseActions.Toggle => ExpandCollapseState.ExpandCollapseState_Expanded,
            _ => throw new InvalidOperationException($"Unsupported expand/collapse action: {action}")
        };

    [SupportedOSPlatform("windows")]
    private static string ApplyExpandCollapseAction(
        IUIAutomationExpandCollapsePattern pattern,
        ExpandCollapseState expectedState)
    {
        if (expectedState == ExpandCollapseState.ExpandCollapseState_Expanded)
        {
            pattern.Expand();
            return "expand";
        }

        pattern.Collapse();
        return "collapse";
    }

    [SupportedOSPlatform("windows")]
    private static bool WaitForExpandCollapseState(
        IUIAutomationExpandCollapsePattern pattern,
        ExpandCollapseState expected,
        CancellationToken cancellationToken,
        out ExpandCollapseState state)
    {
        state = ExpandCollapseState.ExpandCollapseState_LeafNode;
        for (var attempt = 0; attempt < ExpandCollapseVerificationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadExpandCollapseState(pattern, out state) && state == expected)
                return true;

            Thread.Sleep(ExpandCollapseVerificationDelayMilliseconds);
        }

        return TryReadExpandCollapseState(pattern, out state) && state == expected;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadExpandCollapseState(
        IUIAutomationExpandCollapsePattern pattern,
        out ExpandCollapseState state)
    {
        try
        {
            state = pattern.CurrentExpandCollapseState;
            return true;
        }
        catch (COMException)
        {
            state = ExpandCollapseState.ExpandCollapseState_LeafNode;
            return false;
        }
    }

    private static string MapExpandCollapseState(ExpandCollapseState state) =>
        state switch
        {
            ExpandCollapseState.ExpandCollapseState_Collapsed => UiExpandCollapseStates.Collapsed,
            ExpandCollapseState.ExpandCollapseState_Expanded => UiExpandCollapseStates.Expanded,
            ExpandCollapseState.ExpandCollapseState_PartiallyExpanded => UiExpandCollapseStates.PartiallyExpanded,
            ExpandCollapseState.ExpandCollapseState_LeafNode => UiExpandCollapseStates.LeafNode,
            _ => UiExpandCollapseStates.Unknown
        };

    private static CommandResult<UiExpandCollapseResult> ExpandCollapseFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };
}
