namespace LocalMcp.Contracts.Commands;

public sealed record AppOpenCommand : AgentCommand
{
    public required string AppId { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public bool Refresh { get; init; }
    public bool WaitForWindow { get; init; } = true;
    public string? WindowTitleContains { get; init; }
    public int TimeoutMs { get; init; } = 15_000;
    public int PollIntervalMs { get; init; } = 100;
}
