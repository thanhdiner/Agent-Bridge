namespace LocalMcp.Contracts.Commands;

public sealed record SearchFilesCommand : AgentCommand
{
    public required string Path { get; init; }
    public required string Query { get; init; }
    public int MaxResults { get; init; } = 100;
    public int MaxDepth { get; init; } = 4;
}
