namespace LocalMcp.Contracts.Commands;

public sealed record ProcessWaitCommand : AgentCommand
{
    public int? ProcessId { get; init; }
    public string? ProcessName { get; init; }
    public int OccurrenceIndex { get; init; }
    public string Condition { get; init; } = ProcessWaitConditions.Exists;
    public int TimeoutMs { get; init; } = 10_000;
    public int PollIntervalMs { get; init; } = 200;
}
