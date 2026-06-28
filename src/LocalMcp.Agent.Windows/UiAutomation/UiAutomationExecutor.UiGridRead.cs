using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const int MaxGridCellCharacters = 1024;

    public async Task<CommandResult<UiGridReadResult>> GridReadAsync(
        string windowHandle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        int rowStart,
        int rowCount,
        int columnStart,
        int columnCount,
        int maxCells,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return GridReadFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrWhiteSpace(automationId)
            && string.IsNullOrWhiteSpace(name)
            && string.IsNullOrWhiteSpace(controlType))
            return GridReadFailure(commandId, ErrorCodes.InvalidRequest, "automationId, name, or controlType is required.");
        if (automationId?.Length > 1024 || name?.Length > 1024 || controlType?.Length > 128)
            return GridReadFailure(commandId, ErrorCodes.InvalidRequest, "Selector values exceed their maximum lengths.");
        if (occurrenceIndex is < 0 or > 1000)
            return GridReadFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (rowStart is < 0 or > 1_000_000)
            return GridReadFailure(commandId, ErrorCodes.InvalidRequest, "rowStart must be between 0 and 1000000.");
        if (rowCount is < 1 or > 1000)
            return GridReadFailure(commandId, ErrorCodes.InvalidRequest, "rowCount must be between 1 and 1000.");
        if (columnStart is < 0 or > 100_000)
            return GridReadFailure(commandId, ErrorCodes.InvalidRequest, "columnStart must be between 0 and 100000.");
        if (columnCount is < 1 or > 1000)
            return GridReadFailure(commandId, ErrorCodes.InvalidRequest, "columnCount must be between 1 and 1000.");
        if (maxCells is < 1 or > 1000)
            return GridReadFailure(commandId, ErrorCodes.InvalidRequest, "maxCells must be between 1 and 1000.");
        if ((long)rowCount * columnCount > maxCells)
            return GridReadFailure(commandId, ErrorCodes.UiGridLimitExceeded, "rowCount multiplied by columnCount exceeds maxCells.");
        if (!OperatingSystem.IsWindows())
            return GridReadFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI grid reading is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => GridReadWindows(
                    handle,
                    automationId?.Trim(),
                    name?.Trim(),
                    controlType?.Trim(),
                    occurrenceIndex,
                    rowStart,
                    rowCount,
                    columnStart,
                    columnCount,
                    focusWindow,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return GridReadFailure(commandId, ErrorCodes.CommandCancelled, "The UI grid read request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation grid read failure for command {CommandId}", commandId);
            return GridReadFailure(commandId, ErrorCodes.UiGridReadFailed, "Windows UI Automation could not read the requested grid.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI grid read failure for command {CommandId}", commandId);
            return GridReadFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while reading the grid.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiGridReadResult> GridReadWindows(
        IntPtr handle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        int rowStart,
        int requestedRowCount,
        int columnStart,
        int requestedColumnCount,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return GridReadFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        object? gridPatternObject = null;
        object? tablePatternObject = null;
        try
        {
            automation = CreateAutomationClient();
            root = automation.ElementFromHandle(handle);
            if (root is null)
                return GridReadFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");
            if (focusWindow)
                root.SetFocus();

            walker = automation.ControlViewWalker;
            var seen = 0;
            var visited = 0;
            match = FindTarget(root, walker, automationId, name, controlType, occurrenceIndex, ref seen, ref visited, cancellationToken);
            if (match is null)
                return GridReadFailure(commandId, ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");
            if (focusWindow)
                match.SetFocus();

            gridPatternObject = TryGetGridPattern(match, UIA_PatternIds.UIA_GridPatternId);
            tablePatternObject = TryGetGridPattern(match, UIA_PatternIds.UIA_TablePatternId);
            var gridPattern = gridPatternObject as IUIAutomationGridPattern
                ?? tablePatternObject as IUIAutomationGridPattern;
            var tablePattern = tablePatternObject as IUIAutomationTablePattern;
            if (gridPattern is null)
                return GridReadFailure(commandId, ErrorCodes.UiGridNotSupported, "The matched control does not expose GridPattern or TablePattern.");
            if (!TryReadGridDimensions(gridPattern, out var totalRows, out var totalColumns))
                return GridReadFailure(commandId, ErrorCodes.UiGridReadFailed, "The grid dimensions could not be read.");
            if (totalRows < 0 || totalColumns < 0)
                return GridReadFailure(commandId, ErrorCodes.UiGridReadFailed, "The grid reported invalid dimensions.");

            return BuildGridReadResult(
                handle, root, match, gridPattern, tablePattern, occurrenceIndex,
                rowStart, requestedRowCount, columnStart, requestedColumnCount,
                totalRows, totalColumns, commandId, cancellationToken);
        }
        finally
        {
            ReleaseComObject(tablePatternObject);
            ReleaseComObject(gridPatternObject);
            if (!ReferenceEquals(match, root))
                ReleaseComObject(match);
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiGridReadResult> BuildGridReadResult(
        IntPtr handle,
        IUIAutomationElement root,
        IUIAutomationElement match,
        IUIAutomationGridPattern gridPattern,
        IUIAutomationTablePattern? tablePattern,
        int occurrenceIndex,
        int rowStart,
        int requestedRowCount,
        int columnStart,
        int requestedColumnCount,
        int totalRows,
        int totalColumns,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if ((totalRows > 0 && rowStart >= totalRows)
            || (totalColumns > 0 && columnStart >= totalColumns))
        {
            return GridReadFailure(
                commandId,
                ErrorCodes.UiGridCellNotAvailable,
                "rowStart or columnStart is outside the current grid dimensions.");
        }

        var returnedRows = totalRows == 0 ? 0 : Math.Min(requestedRowCount, totalRows - rowStart);
        var returnedColumns = totalColumns == 0 ? 0 : Math.Min(requestedColumnCount, totalColumns - columnStart);
        var truncated = rowStart > 0
            || columnStart > 0
            || rowStart + returnedRows < totalRows
            || columnStart + returnedColumns < totalColumns;
        var patternsUsed = new HashSet<string>(StringComparer.Ordinal) { "grid" };
        if (tablePattern is not null)
            patternsUsed.Add("table");

        var rowHeaders = tablePattern is null
            ? []
            : ReadGridHeaders(tablePattern, rows: true, rowStart, returnedRows);
        var columnHeaders = tablePattern is null
            ? []
            : ReadGridHeaders(tablePattern, rows: false, columnStart, returnedColumns);
        var cells = new List<UiGridCell>(returnedRows * returnedColumns);
        var unavailableCellCount = 0;

        for (var row = rowStart; row < rowStart + returnedRows; row++)
        {
            for (var column = columnStart; column < columnStart + returnedColumns; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cell = ReadGridCell(gridPattern, row, column, patternsUsed);
                if (!cell.Available)
                {
                    unavailableCellCount++;
                    truncated = true;
                }
                cells.Add(cell);
            }
        }

        return new CommandResult<UiGridReadResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new UiGridReadResult
            {
                WindowHandle = FormatWindowHandle(handle),
                ProcessId = ReadOrDefault(() => root.CurrentProcessId, 0),
                Name = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty)),
                AutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty)),
                ControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0)),
                Bounds = ReadBounds(match),
                OccurrenceIndex = occurrenceIndex,
                RowCount = totalRows,
                ColumnCount = totalColumns,
                RowStart = rowStart,
                RequestedRowCount = requestedRowCount,
                ColumnStart = columnStart,
                RequestedColumnCount = requestedColumnCount,
                ReturnedRows = returnedRows,
                ReturnedColumns = returnedColumns,
                CellCount = cells.Count,
                UnavailableCellCount = unavailableCellCount,
                RowHeaders = rowHeaders,
                ColumnHeaders = columnHeaders,
                Cells = cells,
                PatternsUsed = patternsUsed.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                Truncated = truncated
            }
        };
    }

    [SupportedOSPlatform("windows")]
    private static object? TryGetGridPattern(IUIAutomationElement element, int patternId)
    {
        try
        {
            return element.GetCurrentPattern(patternId);
        }
        catch (COMException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadGridDimensions(
        IUIAutomationGridPattern pattern,
        out int rowCount,
        out int columnCount)
    {
        try
        {
            rowCount = pattern.CurrentRowCount;
            columnCount = pattern.CurrentColumnCount;
            return true;
        }
        catch (COMException)
        {
            rowCount = 0;
            columnCount = 0;
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> ReadGridHeaders(
        IUIAutomationTablePattern tablePattern,
        bool rows,
        int start,
        int count)
    {
        IUIAutomationElementArray? headers = null;
        try
        {
            headers = rows
                ? tablePattern.GetCurrentRowHeaders()
                : tablePattern.GetCurrentColumnHeaders();
            if (headers is null)
                return [];

            var values = new List<string>(count);
            var length = headers.Length;
            for (var index = start; index < start + count; index++)
            {
                if (index >= length)
                {
                    values.Add(string.Empty);
                    continue;
                }

                IUIAutomationElement? header = null;
                try
                {
                    header = headers.GetElement(index);
                    if (header is null)
                    {
                        values.Add(string.Empty);
                        continue;
                    }

                    var name = LimitMetadata(ReadOrDefault(() => header.CurrentName, string.Empty));
                    if (string.IsNullOrEmpty(name))
                    {
                        var isPassword = ReadOrDefault(() => header.CurrentIsPassword != 0, false);
                        name = LimitMetadata(ReadValue(header, isPassword, out _) ?? string.Empty);
                    }
                    values.Add(name);
                }
                catch (COMException)
                {
                    values.Add(string.Empty);
                }
                finally
                {
                    ReleaseComObject(header);
                }
            }
            return values;
        }
        catch (COMException)
        {
            return [];
        }
        finally
        {
            ReleaseComObject(headers);
        }
    }

    [SupportedOSPlatform("windows")]
    private static UiGridCell ReadGridCell(
        IUIAutomationGridPattern gridPattern,
        int requestedRow,
        int requestedColumn,
        HashSet<string> patternsUsed)
    {
        IUIAutomationElement? item = null;
        try
        {
            item = gridPattern.GetItem(requestedRow, requestedColumn);
            if (item is null)
                return UnavailableGridCell(requestedRow, requestedColumn);

            var realized = TryRealizeGridItem(item, patternsUsed);
            var row = requestedRow;
            var column = requestedColumn;
            var rowSpan = 1;
            var columnSpan = 1;
            ReadGridItemCoordinates(item, ref row, ref column, ref rowSpan, ref columnSpan, patternsUsed);

            var isPassword = ReadOrDefault(() => item.CurrentIsPassword != 0, false);
            var value = ReadValue(item, isPassword, out var valueTruncated);
            if (value is not null && value.Length > MaxGridCellCharacters)
            {
                value = value[..MaxGridCellCharacters];
                valueTruncated = true;
            }
            var selected = ReadGridCellSelected(item, patternsUsed);

            return new UiGridCell
            {
                Row = row,
                Column = column,
                RowSpan = rowSpan,
                ColumnSpan = columnSpan,
                Name = LimitMetadata(ReadOrDefault(() => item.CurrentName, string.Empty)),
                AutomationId = LimitMetadata(ReadOrDefault(() => item.CurrentAutomationId, string.Empty)),
                ControlType = GetControlTypeName(ReadOrDefault(() => item.CurrentControlType, 0)),
                Value = value,
                ValueTruncated = valueTruncated,
                Enabled = ReadOrDefault(() => item.CurrentIsEnabled != 0, false),
                Selected = selected,
                IsPassword = isPassword,
                Available = true,
                Realized = realized
            };
        }
        catch (COMException)
        {
            return UnavailableGridCell(requestedRow, requestedColumn);
        }
        finally
        {
            ReleaseComObject(item);
        }
    }

    private static UiGridCell UnavailableGridCell(int row, int column) => new()
    {
        Row = row,
        Column = column,
        Name = string.Empty,
        AutomationId = string.Empty,
        ControlType = "Unknown",
        Available = false
    };

    [SupportedOSPlatform("windows")]
    private static bool TryRealizeGridItem(IUIAutomationElement item, HashSet<string> patternsUsed)
    {
        object? patternObject = null;
        try
        {
            patternObject = item.GetCurrentPattern(UIA_PatternIds.UIA_VirtualizedItemPatternId);
            if (patternObject is not IUIAutomationVirtualizedItemPattern virtualizedItemPattern)
                return false;

            virtualizedItemPattern.Realize();
            patternsUsed.Add("virtualized-item");
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
    private static void ReadGridItemCoordinates(
        IUIAutomationElement item,
        ref int row,
        ref int column,
        ref int rowSpan,
        ref int columnSpan,
        HashSet<string> patternsUsed)
    {
        object? gridItemObject = null;
        object? tableItemObject = null;
        try
        {
            gridItemObject = item.GetCurrentPattern(UIA_PatternIds.UIA_GridItemPatternId);
            if (gridItemObject is IUIAutomationGridItemPattern gridItemPattern)
            {
                row = Math.Max(0, gridItemPattern.CurrentRow);
                column = Math.Max(0, gridItemPattern.CurrentColumn);
                rowSpan = Math.Max(1, gridItemPattern.CurrentRowSpan);
                columnSpan = Math.Max(1, gridItemPattern.CurrentColumnSpan);
                patternsUsed.Add("grid-item");
            }

            tableItemObject = item.GetCurrentPattern(UIA_PatternIds.UIA_TableItemPatternId);
            if (tableItemObject is IUIAutomationTableItemPattern)
                patternsUsed.Add("table-item");
        }
        catch (COMException)
        {
        }
        finally
        {
            ReleaseComObject(tableItemObject);
            ReleaseComObject(gridItemObject);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool? ReadGridCellSelected(
        IUIAutomationElement item,
        HashSet<string> patternsUsed)
    {
        object? patternObject = null;
        try
        {
            patternObject = item.GetCurrentPattern(UIA_PatternIds.UIA_SelectionItemPatternId);
            if (patternObject is not IUIAutomationSelectionItemPattern selectionItemPattern)
                return null;

            patternsUsed.Add("selection-item");
            return selectionItemPattern.CurrentIsSelected != 0;
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

    private static CommandResult<UiGridReadResult> GridReadFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };
}
