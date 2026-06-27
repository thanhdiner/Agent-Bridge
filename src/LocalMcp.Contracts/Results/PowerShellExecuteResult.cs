namespace LocalMcp.Contracts.Results;

public sealed record PowerShellExecuteResult
{
    public required string WorkingDirectory { get; init; }
    public bool Success { get; init; }
    public int? ExitCode { get; init; }
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    public long DurationMs { get; init; }
    public bool TimedOut { get; init; }
    public bool Truncated { get; init; }
    public int BytesReturned { get; init; }
}