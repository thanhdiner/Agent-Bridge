namespace LocalMcp.Contracts.Results;
public sealed record UiClickResult
{
    public required string WindowHandle { get; init; }
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public required string ClickMethod { get; init; }
    public int OccurrenceIndex { get; init; }
}