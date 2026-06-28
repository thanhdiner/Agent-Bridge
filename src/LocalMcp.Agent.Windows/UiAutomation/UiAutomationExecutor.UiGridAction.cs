using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{

    public async Task<CommandResult<UiGridSelectResult>> GridSelectAsync(
        string windowHandle,
        string action,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        int row,
        int column,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return GridSelectFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (!UiGridSelectActions.TryNormalize(action, out var normalizedAction))
            return GridSelectFailure(commandId, ErrorCodes.InvalidRequest, "action must be one of: select, add, remove, activate.");
        if (string.IsNullOrWhiteSpace(automationId)
            && string.IsNullOrWhiteSpace(name)
            && string.IsNullOrWhiteSpace(controlType))
            return GridSelectFailure(commandId, ErrorCodes.InvalidRequest, "automationId, name, or controlType is required.");
        if (automationId?.Length > 1024 || name?.Length > 1024 || controlType?.Length > 128)
            return GridSelectFailure(commandId, ErrorCodes.InvalidRequest, "Selector values exceed their maximum lengths.");
        if (occurrenceIndex is < 0 or > 1000)
            return GridSelectFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (row is < 0 or > 1_000_000)
            return GridSelectFailure(commandId, ErrorCodes.InvalidRequest, "row must be between 0 and 1000000.");
        if (column is < 0 or > 100_000)
            return GridSelectFailure(commandId, ErrorCodes.InvalidRequest, "column must be between 0 and 100000.");
        if (!OperatingSystem.IsWindows())
            return GridSelectFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI grid selection is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => GridSelectWindows(
                    handle,
                    normalizedAction,
                    automationId?.Trim(),
                    name?.Trim(),
                    controlType?.Trim(),
                    occurrenceIndex,
                    row,
                    column,
                    focusWindow,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return GridSelectFailure(commandId, ErrorCodes.CommandCancelled, "The UI grid selection request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation grid selection failure for command {CommandId}", commandId);
            return GridSelectFailure(commandId, ErrorCodes.UiGridSelectionFailed, "Windows UI Automation could not complete the requested grid action.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI grid selection failure for command {CommandId}", commandId);
            return GridSelectFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while acting on the grid item.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiGridSelectResult> GridSelectWindows(
        IntPtr handle,
        string action,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        int requestedRow,
        int requestedColumn,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return GridSelectFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? grid = null;
        IUIAutomationElement? item = null;
        object? gridPatternObject = null;
        object? tablePatternObject = null;
        try
        {
            automation = CreateAutomationClient();
            root = automation.ElementFromHandle(handle);
            if (root is null)
                return GridSelectFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");
            if (focusWindow)
                root.SetFocus();

            walker = automation.ControlViewWalker;
            var seen = 0;
            var visited = 0;
            grid = FindTarget(root, walker, automationId, name, controlType, occurrenceIndex, ref seen, ref visited, cancellationToken);
            if (grid is null)
                return GridSelectFailure(commandId, ErrorCodes.UiElementNotFound, "No grid matched the supplied selector and occurrenceIndex.");

            gridPatternObject = TryGetGridPattern(grid, UIA_PatternIds.UIA_GridPatternId);
            tablePatternObject = TryGetGridPattern(grid, UIA_PatternIds.UIA_TablePatternId);
            var gridPattern = gridPatternObject as IUIAutomationGridPattern
                ?? tablePatternObject as IUIAutomationGridPattern;
            if (gridPattern is null)
                return GridSelectFailure(commandId, ErrorCodes.UiGridNotSupported, "The matched control does not expose GridPattern or TablePattern.");

            if (!TryReadGridDimensions(gridPattern, out var totalRows, out var totalColumns))
                return GridSelectFailure(commandId, ErrorCodes.UiGridSelectionFailed, "The grid dimensions could not be read.");
            if (requestedRow >= totalRows || requestedColumn >= totalColumns)
                return GridSelectFailure(commandId, ErrorCodes.UiGridItemNotAvailable, "The requested row or column is outside the current grid dimensions.");

            item = gridPattern.GetItem(requestedRow, requestedColumn);
            if (item is null)
                return GridSelectFailure(commandId, ErrorCodes.UiGridItemNotAvailable, "The requested grid item is not currently available.");

            return BuildGridActionResult(
                handle, root, grid, item, tablePatternObject is IUIAutomationTablePattern,
                action, occurrenceIndex, requestedRow, requestedColumn,
                focusWindow, commandId, cancellationToken);
        }
        finally
        {
            ReleaseComObject(item);
            ReleaseComObject(tablePatternObject);
            ReleaseComObject(gridPatternObject);
            if (!ReferenceEquals(grid, root))
                ReleaseComObject(grid);
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiGridSelectResult> BuildGridActionResult(
        IntPtr handle,
        IUIAutomationElement root,
        IUIAutomationElement grid,
        IUIAutomationElement item,
        bool tablePatternAvailable,
        string action,
        int occurrenceIndex,
        int requestedRow,
        int requestedColumn,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!ReadOrDefault(() => item.CurrentIsEnabled != 0, false))
            return GridSelectFailure(commandId, ErrorCodes.UiGridSelectionFailed, "The requested grid item is disabled.");

        var patternsUsed = new HashSet<string>(StringComparer.Ordinal) { "grid" };
        if (tablePatternAvailable)
            patternsUsed.Add("table");

        var realized = TryRealizeGridItem(item, patternsUsed);
        var scrolledIntoView = TryScrollSelectionItemIntoView(item, ReadBounds(item));
        if (scrolledIntoView)
            patternsUsed.Add("scroll-item");
        if (focusWindow)
            TryFocusGridItem(item);

        var row = requestedRow;
        var column = requestedColumn;
        var rowSpan = 1;
        var columnSpan = 1;
        ReadGridItemCoordinates(item, ref row, ref column, ref rowSpan, ref columnSpan, patternsUsed);

        object? selectionPatternObject = null;
        object? invokePatternObject = null;
        try
        {
            selectionPatternObject = TryGetGridItemPattern(item, UIA_PatternIds.UIA_SelectionItemPatternId);
            invokePatternObject = TryGetGridItemPattern(item, UIA_PatternIds.UIA_InvokePatternId);
            var selectionPattern = selectionPatternObject as IUIAutomationSelectionItemPattern;
            var invokePattern = invokePatternObject as IUIAutomationInvokePattern;
            if (selectionPattern is not null)
                patternsUsed.Add("selection-item");
            if (invokePattern is not null)
                patternsUsed.Add("invoke");

            if (!TryApplyGridItemAction(
                    action,
                    selectionPattern,
                    invokePattern,
                    cancellationToken,
                    out var method,
                    out var selectedBefore,
                    out var selectedAfter,
                    out var actionErrorCode,
                    out var actionErrorMessage))
            {
                return GridSelectFailure(commandId, actionErrorCode!, actionErrorMessage!);
            }

            var isPassword = ReadOrDefault(() => item.CurrentIsPassword != 0, false);
            var value = ReadValue(item, isPassword, out var valueTruncated);
            if (value is not null && value.Length > MaxGridCellCharacters)
            {
                value = value[..MaxGridCellCharacters];
                valueTruncated = true;
            }

            return BuildGridSelectSuccess(
                handle, root, grid, item, occurrenceIndex,
                row, column, rowSpan, columnSpan,
                action, method, selectedBefore, selectedAfter,
                realized, scrolledIntoView, value, valueTruncated,
                patternsUsed, commandId);
        }
        finally
        {
            ReleaseComObject(invokePatternObject);
            ReleaseComObject(selectionPatternObject);
        }
    }

    [SupportedOSPlatform("windows")]
    private static object? TryGetGridItemPattern(IUIAutomationElement item, int patternId)
    {
        try
        {
            return item.GetCurrentPattern(patternId);
        }
        catch (COMException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryFocusGridItem(IUIAutomationElement item)
    {
        try
        {
            item.SetFocus();
        }
        catch (COMException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryApplyGridItemAction(
        string action,
        IUIAutomationSelectionItemPattern? selectionPattern,
        IUIAutomationInvokePattern? invokePattern,
        CancellationToken cancellationToken,
        out string method,
        out bool? selectedBefore,
        out bool? selectedAfter,
        out string? errorCode,
        out string? errorMessage)
    {
        method = string.Empty;
        selectedBefore = null;
        selectedAfter = null;
        errorCode = null;
        errorMessage = null;

        if (action == UiGridSelectActions.Activate && invokePattern is not null)
        {
            if (selectionPattern is not null && TryReadSelected(selectionPattern, out var beforeInvoke))
                selectedBefore = beforeInvoke;
            invokePattern.Invoke();
            method = "invoke";
            if (selectionPattern is not null && TryReadSelected(selectionPattern, out var afterInvoke))
                selectedAfter = afterInvoke;
            return true;
        }

        if (selectionPattern is null)
        {
            errorCode = ErrorCodes.UiGridSelectionNotSupported;
            errorMessage = "The requested grid item does not expose SelectionItemPattern or an activation fallback.";
            return false;
        }
        if (!TryReadSelected(selectionPattern, out var currentSelected))
        {
            errorCode = ErrorCodes.UiGridSelectionFailed;
            errorMessage = "The grid item selected state could not be read before the action.";
            return false;
        }

        selectedBefore = currentSelected;
        var expectedSelected = action == UiGridSelectActions.Activate
            ? true
            : UiGridSelectActions.ExpectedSelected(action)!.Value;
        if (currentSelected == expectedSelected)
        {
            method = "no-op";
            selectedAfter = currentSelected;
            return true;
        }

        method = action == UiGridSelectActions.Activate
            ? ApplyGridActivateFallback(selectionPattern)
            : ApplySelectionAction(selectionPattern, action);
        if (!WaitForSelectedState(selectionPattern, expectedSelected, cancellationToken, out var verifiedSelected))
        {
            errorCode = ErrorCodes.UiGridSelectionVerificationFailed;
            errorMessage = "The grid item selected state did not match during verification.";
            return false;
        }

        selectedAfter = verifiedSelected;
        return true;
    }

    private static string ApplyGridActivateFallback(IUIAutomationSelectionItemPattern pattern)
    {
        pattern.Select();
        return "select-fallback";
    }

    private static CommandResult<UiGridSelectResult> BuildGridSelectSuccess(
        IntPtr handle,
        IUIAutomationElement root,
        IUIAutomationElement grid,
        IUIAutomationElement item,
        int occurrenceIndex,
        int row,
        int column,
        int rowSpan,
        int columnSpan,
        string action,
        string method,
        bool? selectedBefore,
        bool? selectedAfter,
        bool realized,
        bool scrolledIntoView,
        string? value,
        bool valueTruncated,
        IReadOnlyCollection<string> patternsUsed,
        Guid commandId)
    {
        return new CommandResult<UiGridSelectResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new UiGridSelectResult
            {
                WindowHandle = FormatWindowHandle(handle),
                ProcessId = ReadOrDefault(() => root.CurrentProcessId, 0),
                GridName = LimitMetadata(ReadOrDefault(() => grid.CurrentName, string.Empty)),
                GridAutomationId = LimitMetadata(ReadOrDefault(() => grid.CurrentAutomationId, string.Empty)),
                GridControlType = GetControlTypeName(ReadOrDefault(() => grid.CurrentControlType, 0)),
                GridOccurrenceIndex = occurrenceIndex,
                Row = row,
                Column = column,
                RowSpan = rowSpan,
                ColumnSpan = columnSpan,
                Name = LimitMetadata(ReadOrDefault(() => item.CurrentName, string.Empty)),
                AutomationId = LimitMetadata(ReadOrDefault(() => item.CurrentAutomationId, string.Empty)),
                ControlType = GetControlTypeName(ReadOrDefault(() => item.CurrentControlType, 0)),
                Bounds = ReadBounds(item),
                Value = value,
                ValueTruncated = valueTruncated,
                Action = action,
                Method = method,
                SelectedBefore = selectedBefore,
                SelectedAfter = selectedAfter,
                Verified = true,
                Realized = realized,
                ScrolledIntoView = scrolledIntoView,
                PatternsUsed = patternsUsed.OrderBy(entry => entry, StringComparer.Ordinal).ToArray()
            }
        };
    }

    private static CommandResult<UiGridSelectResult> GridSelectFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };
}
