namespace LocalMcp.Contracts.Commands;

public sealed record ListDirectoryCommand : AgentCommand
{
    public required string Path { get; init; }
    public bool IncludeHidden { get; init; } = false;
}
