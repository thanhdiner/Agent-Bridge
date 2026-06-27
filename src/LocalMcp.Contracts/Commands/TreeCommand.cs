namespace LocalMcp.Contracts.Commands;

public sealed record TreeCommand : AgentCommand
{
    public required string Path { get; init; }
    public int MaxDepth { get; init; } = 4;
    public int MaxEntries { get; init; } = 1000;
    public bool IncludeHidden { get; init; } = false;
}
