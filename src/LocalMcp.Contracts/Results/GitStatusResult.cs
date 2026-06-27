namespace LocalMcp.Contracts.Results;

public sealed record GitStatusResult
{
    public required string RepositoryRoot { get; init; }
    public string? Branch { get; init; }
    public bool DetachedHead { get; init; }
    public string? HeadCommit { get; init; }
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public bool IncludeUntracked { get; init; }
    public bool IsClean { get; init; }
    public required List<GitStatusEntry> Entries { get; init; }
    public int OmittedEntries { get; init; }
    public bool Truncated { get; init; }
}

public sealed record GitStatusEntry
{
    public required string Path { get; init; }
    public string? OriginalPath { get; init; }
    public required string Status { get; init; }
    public required string IndexStatus { get; init; }
    public required string WorkTreeStatus { get; init; }
    public bool IsUntracked { get; init; }
    public bool IsConflict { get; init; }
}
