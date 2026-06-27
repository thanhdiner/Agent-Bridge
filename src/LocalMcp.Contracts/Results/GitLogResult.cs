namespace LocalMcp.Contracts.Results;

public sealed record GitLogResult
{
    public required string RepositoryRoot { get; init; }
    public string? Branch { get; init; }
    public IReadOnlyList<GitLogCommitResult> Commits { get; init; } = [];
    public bool Truncated { get; init; }
}

public sealed record GitLogCommitResult
{
    public required string Hash { get; init; }
    public required string ShortHash { get; init; }
    public IReadOnlyList<string> Parents { get; init; } = [];
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }
    public DateTimeOffset AuthoredAt { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public int? FilesChanged { get; init; }
    public int? Insertions { get; init; }
    public int? Deletions { get; init; }
}
