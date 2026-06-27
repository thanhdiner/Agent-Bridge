namespace LocalMcp.Contracts.Results;

public sealed record SearchContextResult
{
    public required List<SearchContextMatch> Matches { get; init; }
    public bool Truncated { get; init; }
}

public sealed record SearchContextMatch
{
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required int LineNumber { get; init; }
    public required string MatchedText { get; init; }
    public required string LineText { get; init; }
    public required List<string> BeforeLines { get; init; }
    public required List<string> AfterLines { get; init; }
    public required string Sha256 { get; init; }
}
