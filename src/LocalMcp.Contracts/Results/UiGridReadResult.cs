namespace LocalMcp.Contracts.Results;

public sealed record UiGridReadResult
{
    public required string WindowHandle { get; init; }
    public int ProcessId { get; init; }
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public int OccurrenceIndex { get; init; }
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
    public int RowStart { get; init; }
    public int RequestedRowCount { get; init; }
    public int ColumnStart { get; init; }
    public int RequestedColumnCount { get; init; }
    public int ReturnedRows { get; init; }
    public int ReturnedColumns { get; init; }
    public int CellCount { get; init; }
    public int UnavailableCellCount { get; init; }
    public required IReadOnlyList<string> RowHeaders { get; init; }
    public required IReadOnlyList<string> ColumnHeaders { get; init; }
    public required IReadOnlyList<UiGridCell> Cells { get; init; }
    public required IReadOnlyList<string> PatternsUsed { get; init; }
    public bool Truncated { get; init; }
}

public sealed record UiGridCell
{
    public int Row { get; init; }
    public int Column { get; init; }
    public int RowSpan { get; init; } = 1;
    public int ColumnSpan { get; init; } = 1;
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public string? Value { get; init; }
    public bool ValueTruncated { get; init; }
    public bool Enabled { get; init; }
    public bool? Selected { get; init; }
    public bool IsPassword { get; init; }
    public bool Available { get; init; }
    public bool Realized { get; init; }
}
