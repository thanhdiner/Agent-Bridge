namespace LocalMcp.Contracts.Commands;

public sealed record SearchFilesCommand : AgentCommand
{
    public required string Path { get; init; }
    public required string Query { get; init; }
    public required string Mode { get; init; } // "name" | "content"
    public string? FilePattern { get; init; }
    public bool CaseSensitive { get; init; } = false;
    public int MaxResults { get; init; } = 100;
    public long MaxFileBytes { get; init; } = 1048576; // 1MB
}
