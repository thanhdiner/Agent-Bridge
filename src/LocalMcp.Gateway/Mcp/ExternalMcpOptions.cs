namespace LocalMcp.Gateway.Mcp;

public sealed class ExternalMcpOptions
{
    public const string SectionName = "ExternalMcp";

    public Dictionary<string, ExternalMcpServerOptions> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? CatalogCachePath { get; set; }

    public int MaxConcurrentWarmups { get; set; } = 2;

    public int FailureCooldownSeconds { get; set; } = 60;
}

public sealed class ExternalMcpServerOptions
{
    public bool Enabled { get; set; } = true;

    public string Command { get; set; } = string.Empty;

    public List<string> Args { get; set; } = [];

    public string? WorkingDirectory { get; set; }

    public int InitializeTimeoutSeconds { get; set; } = 60;

    public int ToolCallTimeoutSeconds { get; set; } = 120;

    public bool InitializeOnStartup { get; set; }
}
