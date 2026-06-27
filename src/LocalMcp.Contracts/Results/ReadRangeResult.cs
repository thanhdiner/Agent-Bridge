namespace LocalMcp.Contracts.Results;

public sealed record ReadRangeResult
{
    public required string Path { get; init; }
    public long StartLine { get; init; }
    public long EndLine { get; init; }
    public long TotalLines { get; init; }
    public required string Content { get; init; }
    public bool Truncated { get; init; }
    public required string Sha256 { get; init; }
    public required string Encoding { get; init; }
}
