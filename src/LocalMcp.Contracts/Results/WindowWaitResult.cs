namespace LocalMcp.Contracts.Results;

public sealed record WindowWaitResult
{
    public required string Condition { get; init; }
    public string? ExpectedTitle { get; init; }
    public int WaitedMs { get; init; }
    public int PollCount { get; init; }
    public int OccurrenceIndex { get; init; }
    public bool WindowFound { get; init; }
    public WindowInfo? Window { get; init; }
}
