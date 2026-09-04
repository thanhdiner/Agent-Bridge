using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace LocalMcp.Gateway.Mcp;

public sealed class ExternalMcpCatalogCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<ExternalMcpCatalogCache> _logger;

    public ExternalMcpCatalogCache(
        IOptions<ExternalMcpOptions> options,
        ILogger<ExternalMcpCatalogCache> logger)
    {
        _logger = logger;
        CachePath = string.IsNullOrWhiteSpace(options.Value.CatalogCachePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentBridge",
                "external-mcp-catalog.json")
            : options.Value.CatalogCachePath;
    }

    public string CachePath { get; }

    public ExternalMcpCatalogSnapshot Load()
    {
        try
        {
            if (!File.Exists(CachePath))
                return new ExternalMcpCatalogSnapshot(Array.Empty<Tool>(), Array.Empty<ExternalMcpServerCatalogStatus>());

            var content = File.ReadAllText(CachePath);
            var cached = JsonSerializer.Deserialize<CachedExternalMcpCatalog>(content, JsonOptions);
            return new ExternalMcpCatalogSnapshot(
                cached?.Tools ?? Array.Empty<Tool>(),
                cached?.Servers ?? Array.Empty<ExternalMcpServerCatalogStatus>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not load external MCP catalog cache from {CachePath}", CachePath);
            return new ExternalMcpCatalogSnapshot(Array.Empty<Tool>(), Array.Empty<ExternalMcpServerCatalogStatus>());
        }
    }

    public void Save(ExternalMcpCatalogSnapshot snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var cached = new CachedExternalMcpCatalog(
                snapshot.Tools,
                snapshot.Servers,
                DateTimeOffset.UtcNow);

            File.WriteAllText(CachePath, JsonSerializer.Serialize(cached, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save external MCP catalog cache to {CachePath}", CachePath);
        }
    }

    private sealed record CachedExternalMcpCatalog(
        IReadOnlyList<Tool> Tools,
        IReadOnlyList<ExternalMcpServerCatalogStatus> Servers,
        DateTimeOffset UpdatedAtUtc);
}
