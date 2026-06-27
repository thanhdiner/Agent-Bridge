namespace LocalMcp.Contracts.Results;

/// <summary>
/// Returned by powershell_status and powershell_cancel.
/// Supports incremental output via NextOutputOffset.
/// </summary>
public sealed record PowerShellSessionResult
{
    public required Guid SessionId { get; init; }

    /// <summary>running | completed | failed | cancelled | timedOut</summary>
    public required string State { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>Stdout bytes from OutputOffset up to MaxOutputBytes.</summary>
    public required string Stdout { get; init; }

    /// <summary>Stderr bytes from OutputOffset up to MaxOutputBytes.</summary>
    public required string Stderr { get; init; }

    /// <summary>
    /// The next offset to pass as outputOffset in the following poll call.
    /// When equal to the total accumulated bytes and state is terminal,
    /// all output has been consumed.
    /// </summary>
    public required long NextOutputOffset { get; init; }

    /// <summary>True when accumulated output exceeded MaxOutputBytes and was capped.</summary>
    public bool Truncated { get; init; }
}
