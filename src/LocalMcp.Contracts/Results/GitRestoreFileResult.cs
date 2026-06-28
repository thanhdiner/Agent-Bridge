namespace LocalMcp.Contracts.Results;

public sealed record GitRestoreFileResult
{
    public required string RepositoryRoot { get; init; }
    public required string Path { get; init; }
    public required string Source { get; init; }
    public string? PreviousSha256 { get; init; }
    public required string Sha256 { get; init; }
    public required long Size { get; init; }
    public required bool Changed { get; init; }
}
