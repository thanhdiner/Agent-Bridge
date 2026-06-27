namespace LocalMcp.Contracts.Results;

public sealed record ReadFileResult
{
    public required string Path { get; init; }
    public required string Content { get; init; }
    public required string Encoding { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
}
