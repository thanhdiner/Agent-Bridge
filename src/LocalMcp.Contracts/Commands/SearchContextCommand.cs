namespace LocalMcp.Contracts.Commands;

public sealed record SearchContextCommand : AgentCommand
{
    public required string Path { get; init; }
    public required string Query { get; init; }
    public bool UseRegex { get; init; }
    public bool CaseSensitive { get; init; }
    public List<string> IncludeGlobs { get; init; } = [];
    public List<string> ExcludeGlobs { get; init; } = [];
    public int ContextBefore { get; init; } = 2;
    public int ContextAfter { get; init; } = 2;
    public int MaxResults { get; init; } = 100;
    public int MaxDepth { get; init; } = 4;
}
