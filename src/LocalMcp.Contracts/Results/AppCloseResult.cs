namespace LocalMcp.Contracts.Results;

public sealed record AppCloseResult
{
    public int MatchedCount { get; init; }
    public int CloseRequestedCount { get; init; }
    public int ClosedCount { get; init; }
    public bool Force { get; init; }
    public bool EntireProcessTree { get; init; }
    public int TimeoutMs { get; init; }
    public int ElapsedMs { get; init; }
    public IReadOnlyList<AppCloseProcessResult> Processes { get; init; } = [];
}

public sealed record AppCloseProcessResult
{
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public bool GracefulCloseRequested { get; init; }
    public bool ForceKillRequested { get; init; }
    public bool Closed { get; init; }
    public bool TimedOut { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
