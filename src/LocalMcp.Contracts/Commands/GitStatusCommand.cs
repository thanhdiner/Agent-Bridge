namespace LocalMcp.Contracts.Commands;

public sealed record GitStatusCommand : AgentCommand
{
    public required string Path { get; init; }
    public bool IncludeUntracked { get; init; } = true;
    public int MaxEntries { get; init; } = 1000;
}
