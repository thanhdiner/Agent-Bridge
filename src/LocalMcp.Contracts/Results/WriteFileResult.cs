namespace LocalMcp.Contracts.Results;

public sealed record WriteFileResult
{
    public required string Path { get; init; }
    public required bool Created { get; init; }
    public required int BytesWritten { get; init; }
    public string? PreviousSha256 { get; init; }
    public required string Sha256 { get; init; }
    public required string Encoding { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }
}
