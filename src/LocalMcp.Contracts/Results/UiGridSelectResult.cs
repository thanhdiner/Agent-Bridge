namespace LocalMcp.Contracts.Results;

public sealed record UiGridSelectResult
{
    public required string WindowHandle { get; init; }
    public int ProcessId { get; init; }
    public required string GridName { get; init; }
    public required string GridAutomationId { get; init; }
    public required string GridControlType { get; init; }
    public int GridOccurrenceIndex { get; init; }
    public int Row { get; init; }
    public int Column { get; init; }
    public int RowSpan { get; init; } = 1;
    public int ColumnSpan { get; init; } = 1;
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public string? Value { get; init; }
    public bool ValueTruncated { get; init; }
    public required string Action { get; init; }
    public required string Method { get; init; }
    public bool? SelectedBefore { get; init; }
    public bool? SelectedAfter { get; init; }
    public bool Verified { get; init; }
    public bool Realized { get; init; }
    public bool ScrolledIntoView { get; init; }
    public required IReadOnlyList<string> PatternsUsed { get; init; }
}
