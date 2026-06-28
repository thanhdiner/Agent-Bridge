namespace LocalMcp.Contracts.Results;

public sealed record AppResolveResult
{
    public required string AppId { get; init; }
    public required string NormalizedAppId { get; init; }
    public bool Resolved { get; init; }
    public string? ExecutablePath { get; init; }
    public string? ProcessName { get; init; }
    public string? Source { get; init; }
    public bool CacheHit { get; init; }
    public bool Refreshed { get; init; }
    public int ElapsedMs { get; init; }
    public DateTimeOffset? LastWriteTimeUtc { get; init; }
}
