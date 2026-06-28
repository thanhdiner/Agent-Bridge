namespace LocalMcp.Contracts.Results;

public sealed record AppOpenResult
{
    public required string AppId { get; init; }
    public required string NormalizedAppId { get; init; }
    public required string ExecutablePath { get; init; }
    public string? Source { get; init; }
    public bool CacheHit { get; init; }
    public bool Refreshed { get; init; }
    public int ResolveElapsedMs { get; init; }
    public int LaunchElapsedMs { get; init; }
    public int TotalElapsedMs { get; init; }
    public required AppLaunchResult Launch { get; init; }
}
