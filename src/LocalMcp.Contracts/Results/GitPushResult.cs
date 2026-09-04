namespace LocalMcp.Contracts.Results;

public sealed record GitPushResult
{
    public required string RepositoryRoot { get; init; }
    public string? Branch { get; init; }
    public string? Remote { get; init; }
    public bool SetUpstream { get; init; }
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
    public bool StdoutTruncated { get; init; }
    public bool StderrTruncated { get; init; }
}
