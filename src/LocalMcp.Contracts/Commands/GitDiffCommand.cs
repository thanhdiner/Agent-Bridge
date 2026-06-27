namespace LocalMcp.Contracts.Commands;

public sealed record GitDiffCommand : AgentCommand
{
    public required string Path { get; init; }
    public bool Staged { get; init; }
    public bool IncludeUntracked { get; init; } = true;
    public List<string> PathSpecs { get; init; } = [];
    public int ContextLines { get; init; } = 3;
    public int MaxBytes { get; init; } = 1_048_576;
}
