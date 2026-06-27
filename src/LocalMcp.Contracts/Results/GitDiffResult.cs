namespace LocalMcp.Contracts.Results;

public sealed record GitDiffResult
{
    public required string RepositoryRoot { get; init; }
    public bool Staged { get; init; }
    public bool IncludeUntracked { get; init; }
    public required string Diff { get; init; }
    public int BytesReturned { get; init; }
    public bool Truncated { get; init; }
    public int OmittedFiles { get; init; }
    public required List<GitUntrackedFileResult> UntrackedFiles { get; init; }
}

public sealed record GitUntrackedFileResult
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public bool Included { get; init; }
    public bool Truncated { get; init; }
    public string? Reason { get; init; }
}
