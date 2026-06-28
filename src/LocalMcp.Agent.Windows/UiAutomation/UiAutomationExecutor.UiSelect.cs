using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const int SelectionVerificationAttempts = 10;
    private const int SelectionVerificationDelayMilliseconds = 50;

    public async Task<CommandResult<UiSelectResult>> SelectAsync(
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
            return SelectFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            return SelectFailure(commandId, ErrorCodes.InvalidRequest, "automationId or name is required.");
        if (automationId?.Length > 1024 || name?.Length > 1024 || controlType?.Length > 128)
            return SelectFailure(commandId, ErrorCodes.InvalidRequest, "Selector values exceed their maximum lengths.");
        if (occurrenceIndex is < 0 or > 1000)
            return SelectFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (!UiSelectActions.TryNormalize(action, out var normalizedAction))
            return SelectFailure(commandId, ErrorCodes.InvalidRequest, "action must be one of: select, add, remove.");
        if (!OperatingSystem.IsWindows())
            return SelectFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI selection is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => SelectWindows(
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
            return SelectFailure(commandId, ErrorCodes.CommandCancelled, "The UI selection request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation selection failure for command {CommandId}", commandId);
            return SelectFailure(commandId, ErrorCodes.UiSelectionFailed, "Windows UI Automation could not change the requested selection.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI selection failure for command {CommandId}", commandId);
            return SelectFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while changing selection.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiSelectResult> SelectWindows(
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
            return SelectFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        object? selectionPatternObject = null;
        try
        {
            automation = CreateAutomationClient();
            root = automation.ElementFromHandle(handle);
            if (root is null)
                return SelectFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");

            if (focusWindow)
                root.SetFocus();

            walker = automation.ControlViewWalker;
            var seen = 0;
            var visited = 0;
            match = FindTarget(root, walker, automationId, name, controlType, occurrenceIndex, ref seen, ref visited, cancellationToken);
            if (match is null)
                return SelectFailure(commandId, ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");
            if (!ReadOrDefault(() => match.CurrentIsEnabled != 0, false))
                return SelectFailure(commandId, ErrorCodes.UiSelectionFailed, "The matched control is disabled.");

            var matchedName = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty));
            var matchedAutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty));
            var matchedControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0));
            var bounds = ReadBounds(match);
            var scrolledIntoView = TryScrollSelectionItemIntoView(match, bounds);

            selectionPatternObject = match.GetCurrentPattern(UIA_PatternIds.UIA_SelectionItemPatternId);
            if (selectionPatternObject is not IUIAutomationSelectionItemPattern selectionPattern)
                return SelectFailure(commandId, ErrorCodes.UiSelectionNotSupported, "The matched control does not expose SelectionItemPattern.");

            if (!TryReadSelected(selectionPattern, out var selectedBefore))
                return SelectFailure(commandId, ErrorCodes.UiSelectionFailed, "The selected state could not be read before the action.");

            var expectedSelected = UiSelectActions.ExpectedSelected(action);
            var method = "no-op";
            if (selectedBefore != expectedSelected)
                method = ApplySelectionAction(selectionPattern, action);

            var verified = WaitForSelectedState(selectionPattern, expectedSelected, cancellationToken, out var selectedAfter);
            if (!verified)
                return SelectFailure(commandId, ErrorCodes.UiSelectionVerificationFailed, "The selected state did not match during verification.");

            return new CommandResult<UiSelectResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new UiSelectResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    Name = matchedName,
                    AutomationId = matchedAutomationId,
                    ControlType = matchedControlType,
                    Bounds = ReadBounds(match),
                    Action = action,
                    SelectionMethod = method,
                    SelectedBefore = selectedBefore,
                    SelectedAfter = selectedAfter,
                    Verified = true,
                    ScrolledIntoView = scrolledIntoView,
                    OccurrenceIndex = occurrenceIndex
                }
            };
        }
        finally
        {
            ReleaseComObject(selectionPatternObject);
            if (!ReferenceEquals(match, root))
                ReleaseComObject(match);
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryScrollSelectionItemIntoView(IUIAutomationElement element, UiBounds bounds)
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
    private static string ApplySelectionAction(IUIAutomationSelectionItemPattern pattern, string action)
    {
        switch (action)
        {
            case UiSelectActions.Select:
                pattern.Select();
                return "select";
            case UiSelectActions.Add:
                pattern.AddToSelection();
                return "add-to-selection";
            case UiSelectActions.Remove:
                pattern.RemoveFromSelection();
                return "remove-from-selection";
            default:
                throw new InvalidOperationException($"Unsupported selection action: {action}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool WaitForSelectedState(
        IUIAutomationSelectionItemPattern pattern,
        bool expected,
        CancellationToken cancellationToken,
        out bool selected)
    {
        selected = !expected;
        for (var attempt = 0; attempt < SelectionVerificationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadSelected(pattern, out selected) && selected == expected)
                return true;

            Thread.Sleep(SelectionVerificationDelayMilliseconds);
        }

        return TryReadSelected(pattern, out selected) && selected == expected;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadSelected(IUIAutomationSelectionItemPattern pattern, out bool selected)
    {
        try
        {
            selected = pattern.CurrentIsSelected != 0;
            return true;
        }
        catch (COMException)
        {
            selected = false;
            return false;
        }
    }

    private static CommandResult<UiSelectResult> SelectFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };
}
