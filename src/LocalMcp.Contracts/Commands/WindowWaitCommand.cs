namespace LocalMcp.Contracts.Commands;

public sealed record WindowWaitCommand : AgentCommand
{
    public string? WindowHandle { get; init; }
    public int? ProcessId { get; init; }
    public string? ProcessName { get; init; }
    public string? ClassName { get; init; }
    public string? Title { get; init; }
    public string? TitleContains { get; init; }
    public int OccurrenceIndex { get; init; }
    public string Condition { get; init; } = WindowWaitConditions.Exists;
    public string? ExpectedTitle { get; init; }
    public bool IncludeInvisible { get; init; }
    public int TimeoutMs { get; init; } = 10_000;
    public int PollIntervalMs { get; init; } = 200;
}
