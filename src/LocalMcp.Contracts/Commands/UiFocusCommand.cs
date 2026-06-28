namespace LocalMcp.Contracts.Commands;

public sealed record UiFocusCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public int OccurrenceIndex { get; init; }
}
