namespace LocalMcp.Contracts.Results;

public sealed record ProcessWaitResult
{
    public required string Condition { get; init; }
    public required string CompletionReason { get; init; }
    public required string FinalState { get; init; }
    public int ElapsedMs { get; init; }
    public int WaitedMs { get; init; }
    public int PollCount { get; init; }
    public int OccurrenceIndex { get; init; }
    public bool ProcessFound { get; init; }
    public int? ProcessId { get; init; }
    public string? ProcessName { get; init; }
}
