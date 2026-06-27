namespace LocalMcp.Contracts.Results;

public sealed record SearchMatch
{
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required string MatchType { get; init; } // "name" | "content"
    public int? LineNumber { get; init; }
    public string? LinePreview { get; init; }
}
