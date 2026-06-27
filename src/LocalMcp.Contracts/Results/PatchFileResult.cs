namespace LocalMcp.Contracts.Results;

public sealed record PatchFileResult
{
    public required string Path { get; init; }
    public required int EditsApplied { get; init; }
    public required int ReplacementsMade { get; init; }
    public required int BytesWritten { get; init; }
    public required string PreviousSha256 { get; init; }
    public required string Sha256 { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }
}
