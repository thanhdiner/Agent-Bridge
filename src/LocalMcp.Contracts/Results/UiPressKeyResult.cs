namespace LocalMcp.Contracts.Results;
public sealed record UiPressKeyResult
{
    public required string WindowHandle { get; init; }
    public string? Name { get; init; }
    public string? AutomationId { get; init; }
    public string? ControlType { get; init; }
    public UiBounds? Bounds { get; init; }
    public required string Keys { get; init; }
    public int InputsSent { get; init; }
    public bool TargetedControl { get; init; }
    public int OccurrenceIndex { get; init; }
}