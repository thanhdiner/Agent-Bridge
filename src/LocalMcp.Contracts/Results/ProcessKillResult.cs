namespace LocalMcp.Contracts.Results;

public sealed record ProcessKillResult
{
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public bool EntireProcessTree { get; init; }
    public bool KillRequested { get; init; }
    public bool Exited { get; init; }
    public int TimeoutMs { get; init; }
    public int ElapsedMs { get; init; }
}
