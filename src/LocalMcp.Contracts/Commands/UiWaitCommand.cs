namespace LocalMcp.Contracts.Commands;

public sealed record UiWaitCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public int OccurrenceIndex { get; init; }
    public string Condition { get; init; } = UiWaitConditions.Exists;
    public string? ExpectedValue { get; init; }
    public int TimeoutMs { get; init; } = 10_000;
    public int PollIntervalMs { get; init; } = 200;
}
