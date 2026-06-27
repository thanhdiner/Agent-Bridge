namespace LocalMcp.Contracts.Results;

public sealed record SearchFilesResult
{
    public required List<SearchMatch> Matches { get; init; }
}
