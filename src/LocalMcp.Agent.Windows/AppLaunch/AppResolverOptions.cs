namespace LocalMcp.Agent.Windows.AppLaunch;

public sealed class AppResolverOptions
{
    public const string SectionName = "AppResolver";

    public string? CachePath { get; set; }
    public int MaxCacheEntries { get; set; } = 256;
    public int MaxStartMenuShortcuts { get; set; } = 2000;
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
