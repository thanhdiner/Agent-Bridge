namespace LocalMcp.Contracts.Commands;

public sealed record ListDirectoryCommand : AgentCommand
{
    public required string Path { get; init; }
    public int MaxEntries { get; init; } = 1000;
}
