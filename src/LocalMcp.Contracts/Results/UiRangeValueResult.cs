namespace LocalMcp.Contracts.Results;
public sealed record UiRangeValueResult
{
    public required string WindowHandle { get; init; }
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public required string Action { get; init; }
    public required string Method { get; init; }
    public double ValueBefore { get; init; }
    public double ValueAfter { get; init; }
    public double Minimum { get; init; }
    public double Maximum { get; init; }
    public double SmallChange { get; init; }
    public double LargeChange { get; init; }
    public bool IsReadOnly { get; init; }
    public bool Verified { get; init; }
    public bool ScrolledIntoView { get; init; }
    public int OccurrenceIndex { get; init; }
}
