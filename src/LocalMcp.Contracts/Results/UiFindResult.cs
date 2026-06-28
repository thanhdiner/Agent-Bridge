namespace LocalMcp.Contracts.Results;

public sealed record UiFindResult
{
    public required string WindowHandle { get; init; }
    public int ProcessId { get; init; }
    public required List<UiFindMatch> Matches { get; init; }
    public int Count { get; init; }
    public int VisitedNodes { get; init; }
    public int MaxDepth { get; init; }
    public int MaxResults { get; init; }
    public bool Truncated { get; init; }
}

public sealed record UiFindMatch
{
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public bool Enabled { get; init; }
    public required List<string> Patterns { get; init; }
    public int OccurrenceIndex { get; init; }
    public int Depth { get; init; }
}
