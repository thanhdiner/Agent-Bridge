namespace LocalMcp.Contracts.Results;

/// <summary>
/// Returned immediately by powershell_start. The session continues running in the Agent.
/// Poll with powershell_status using the returned sessionId.
/// </summary>
public sealed record PowerShellStartResult
{
    public required Guid SessionId { get; init; }

    /// <summary>"running" — always this value on success.</summary>
    public required string State { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
}
