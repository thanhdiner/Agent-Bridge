namespace LocalMcp.Contracts.Results;
public sealed record UiTypeTextResult
{
    public required string WindowHandle { get; init; }
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public int CharacterCount { get; init; }
    public int InputsSent { get; init; }
    public bool IsPassword { get; init; }
    public int OccurrenceIndex { get; init; }
}