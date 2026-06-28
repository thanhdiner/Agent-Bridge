namespace LocalMcp.Contracts.Results;

/// <summary>
/// Returned by powershell_status and powershell_cancel.
/// Supports incremental output via NextStdoutOffset and NextStderrOffset.
/// </summary>
public sealed record PowerShellSessionResult
{
    public required Guid SessionId { get; init; }

    /// <summary>running | completed | failed | cancelled | timedOut</summary>
    public required string State { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>Stdout bytes from StdoutOffset up to MaxOutputBytes.</summary>
    public required string Stdout { get; init; }

    /// <summary>Stderr bytes from StderrOffset up to MaxOutputBytes.</summary>
    public required string Stderr { get; init; }

    /// <summary>
    /// The next offset to pass as stdoutOffset in the following poll call.
    /// </summary>
    public required long NextStdoutOffset { get; init; }

    /// <summary>
    /// The next offset to pass as stderrOffset in the following poll call.
    /// </summary>
    public required long NextStderrOffset { get; init; }

    /// <summary>True when accumulated output exceeded MaxOutputBytes and was capped.</summary>
    public bool Truncated { get; init; }

    /// <summary>True when invalid UTF-8 sequences were detected and discarded during execution or at terminal transition.</summary>
    public bool InvalidUtf8 { get; init; }
}
