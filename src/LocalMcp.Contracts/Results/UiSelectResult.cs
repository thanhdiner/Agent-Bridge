namespace LocalMcp.Contracts.Results;

public sealed record UiSelectResult
{
    public required string WindowHandle { get; init; }
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public required string Action { get; init; }
    public required string SelectionMethod { get; init; }
    public bool SelectedBefore { get; init; }
    public bool SelectedAfter { get; init; }
    public bool Verified { get; init; }
    public bool ScrolledIntoView { get; init; }
    public int OccurrenceIndex { get; init; }
}
