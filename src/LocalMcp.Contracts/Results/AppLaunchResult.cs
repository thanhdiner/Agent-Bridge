namespace LocalMcp.Contracts.Results;

public sealed record AppLaunchResult
{
    public required string ExecutablePath { get; init; }
    public required string ProcessName { get; init; }
    public int ProcessId { get; init; }
    public bool Started { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public bool HasExited { get; init; }
    public int? ExitCode { get; init; }
    public bool WaitForWindow { get; init; }
    public bool WindowFound { get; init; }
    public bool WindowWaitTimedOut { get; init; }
    public string? WindowWaitErrorCode { get; init; }
    public string? WindowWaitErrorMessage { get; init; }
    public int WaitedMs { get; init; }
    public int PollCount { get; init; }
    public WindowInfo? Window { get; init; }
}
