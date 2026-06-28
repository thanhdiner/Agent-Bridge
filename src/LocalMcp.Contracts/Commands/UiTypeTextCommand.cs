namespace LocalMcp.Contracts.Commands;
public sealed record UiTypeTextCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public required string Text { get; init; }
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public int OccurrenceIndex { get; init; }
    public bool FocusWindow { get; init; } = true;
}