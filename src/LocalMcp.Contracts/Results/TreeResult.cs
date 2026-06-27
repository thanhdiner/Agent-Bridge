namespace LocalMcp.Contracts.Results;

public sealed record TreeResult
{
    public required string Path { get; init; }
    public required List<TreeEntry> Entries { get; init; }
    public required bool Truncated { get; init; }
}
