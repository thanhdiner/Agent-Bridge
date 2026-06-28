namespace LocalMcp.Contracts.Results;
public sealed record UiScrollResult
{
    public required string WindowHandle { get; init; }
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public required string Direction { get; init; }
    public required string Amount { get; init; }
    public required string ScrollMethod { get; init; }
    public bool HorizontalScrollable { get; init; }
    public bool VerticalScrollable { get; init; }
    public double? HorizontalPercentBefore { get; init; }
    public double? HorizontalPercentAfter { get; init; }
    public double? VerticalPercentBefore { get; init; }
    public double? VerticalPercentAfter { get; init; }
    public double HorizontalViewSize { get; init; }
    public double VerticalViewSize { get; init; }
    public bool Changed { get; init; }
    public int OccurrenceIndex { get; init; }
}
