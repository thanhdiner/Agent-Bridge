namespace LocalMcp.Contracts.Results;

public sealed record GitRefreshIndexResult
{
    public required string RepositoryRoot { get; init; }
    public required string Path { get; init; }
    public required string IndexObjectId { get; init; }
    public required string WorkingTreeObjectId { get; init; }
    public required bool RewrittenFromIndex { get; init; }
    public required bool CleanAfterRefresh { get; init; }
}
