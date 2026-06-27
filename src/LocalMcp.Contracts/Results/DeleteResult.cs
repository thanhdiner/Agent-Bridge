namespace LocalMcp.Contracts.Results;

public sealed record DeleteResult
{
    public required string Path { get; init; }
    public long BytesDeleted { get; init; }
    public string? Sha256 { get; init; }
}
