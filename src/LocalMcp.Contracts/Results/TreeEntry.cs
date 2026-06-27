namespace LocalMcp.Contracts.Results;

public sealed record TreeEntry
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required string Type { get; init; } // "file" | "directory"
    public required int Depth { get; init; }
    public required long SizeBytes { get; init; }
}
