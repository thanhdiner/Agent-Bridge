namespace LocalMcp.Contracts.Commands;

public sealed record UiSetValueCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public required string Value { get; init; }
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public int OccurrenceIndex { get; init; }
    public bool FocusWindow { get; init; } = true;
    public bool Append { get; init; }
}
