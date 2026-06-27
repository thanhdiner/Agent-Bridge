namespace LocalMcp.Contracts.Results;

public sealed record GitShowResult
{
    public required string RepositoryRoot { get; init; }
    public required string Revision { get; init; }
    public required GitShowCommitResult Commit { get; init; }
    public GitShowStatsResult? Stats { get; init; }
    public required string Patch { get; init; }
    public int BytesReturned { get; init; }
    public bool Truncated { get; init; }
}

public sealed record GitShowCommitResult
{
    public required string Hash { get; init; }
    public IReadOnlyList<string> Parents { get; init; } = [];
    public required GitAuthorResult Author { get; init; }
    public DateTimeOffset AuthoredAt { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
}

public sealed record GitAuthorResult
{
    public required string Name { get; init; }
    public required string Email { get; init; }
}

public sealed record GitShowStatsResult
{
    public int FilesChanged { get; init; }
    public int Insertions { get; init; }
    public int Deletions { get; init; }
}
