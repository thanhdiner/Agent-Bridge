using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace LocalMcp.Gateway.Mcp;

public sealed class ToolVisibilityStore
{
    public const int DefaultConnectionMaxEnabledTools = 150;
    public const string ConnectionA = "A";
    public const string ConnectionB = "B";
    public const string ConnectionAndroidA = "AndroidA";
    public const string ConnectionNone = "None";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly ILogger<ToolVisibilityStore> _logger;
    private ToolVisibilityConfig _config;
    private IReadOnlyList<ToolCatalogItem> _catalog = Array.Empty<ToolCatalogItem>();
    private IReadOnlyList<ExternalMcpServerCatalogStatus> _externalServerStatuses = Array.Empty<ExternalMcpServerCatalogStatus>();

    public ToolVisibilityStore(ILogger<ToolVisibilityStore> logger)
        : this(logger, null)
    {
    }

    public ToolVisibilityStore(ILogger<ToolVisibilityStore> logger, string? configPath)
    {
        _logger = logger;
        ConfigPath = string.IsNullOrWhiteSpace(configPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentBridge",
                "tool-visibility.json")
            : configPath;
        _config = LoadOrCreateDefault();
    }

    public string ConfigPath { get; }

    public bool IsCustomMode
    {
        get
        {
            lock (_gate)
            {
                return IsModeCustom(_config.Mode);
            }
        }
    }

    public bool IsToolEnabled(string? toolName) =>
        !string.IsNullOrWhiteSpace(GetToolConnection(toolName));

    public bool IsToolEnabledForConnection(string? toolName, string? connection)
    {
        var normalizedName = NormalizeToolName(toolName);
        var normalizedConnection = NormalizeConnection(connection);
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedConnection is null)
            return false;

        lock (_gate)
        {
            return string.Equals(GetAssignedConnection(_config, normalizedName), normalizedConnection, StringComparison.Ordinal);
        }
    }

    public bool ShouldListExternalServer(string? serverName) => ShouldListExternalServer(serverName, null);

    public bool ShouldListExternalServer(string? serverName, string? connection)
    {
        var normalizedServerName = NormalizeToolName(serverName);
        var normalizedConnection = NormalizeConnection(connection);
        if (string.IsNullOrWhiteSpace(normalizedServerName))
            return false;

        lock (_gate)
        {
            if (_config.NeedsAssignmentMigration)
                return true;

            var prefix = normalizedServerName + ".";
            return _config.ToolAssignments.Any(pair =>
                pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (normalizedConnection is null || string.Equals(pair.Value, normalizedConnection, StringComparison.Ordinal)));
        }
    }

    public string? GetToolConnection(string? toolName)
    {
        var normalizedName = NormalizeToolName(toolName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return null;

        lock (_gate)
        {
            return GetAssignedConnection(_config, normalizedName);
        }
    }

    public void RememberCatalog(IEnumerable<Tool> localTools, IEnumerable<Tool> externalTools)
    {
        RememberCatalog(localTools, externalTools, Array.Empty<ExternalMcpServerCatalogStatus>());
    }

    public void RememberCatalog(
        IEnumerable<Tool> localTools,
        IEnumerable<Tool> externalTools,
        IEnumerable<ExternalMcpServerCatalogStatus> externalServerStatuses)
    {
        var localItems = localTools
            .Where(tool => !tool.Name.StartsWith("android_", StringComparison.OrdinalIgnoreCase))
            .Select(tool => BuildCatalogItem(tool, "local"))
            .ToArray();
        var externalItems = externalTools.Select(tool => BuildCatalogItem(tool, "external")).ToArray();

        lock (_gate)
        {
            if (localItems.Length == 0)
            {
                localItems = _catalog
                    .Where(item => string.Equals(item.Source, "local", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            var items = localItems
                .Concat(externalItems)
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => SourceSortKey(item.Source))
                .ThenBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _catalog = items;
            _externalServerStatuses = externalServerStatuses
                .OrderBy(status => status.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (_config.NeedsAssignmentMigration)
            {
                _config = MigrateAssignments(_config, _catalog);
                TryPersist(_config);
            }
        }
    }

    public ToolVisibilitySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return BuildSnapshot(_config, _catalog);
        }
    }

    public async Task<ToolVisibilitySnapshot> SaveAsync(ToolVisibilityUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mode = NormalizeMode(request.Mode);
        var assignments = NormalizeAssignments(request.ToolAssignments, request.EnabledTools);
        ValidateConnectionCounts(assignments);
        var updatedAtUtc = DateTimeOffset.UtcNow;

        var config = new ToolVisibilityConfig
        {
            Mode = mode,
            EnabledTools = assignments.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
            ToolAssignments = assignments,
            UpdatedAtUtc = updatedAtUtc,
            NeedsAssignmentMigration = false
        };

        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(config, JsonOptions), cancellationToken);

        lock (_gate)
        {
            _config = config;
            return BuildSnapshot(_config, _catalog);
        }
    }

    private ToolVisibilityConfig LoadOrCreateDefault()
    {
        try
        {
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            if (!File.Exists(ConfigPath))
            {
                var defaultConfig = CreateDefaultConfig();
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(defaultConfig, JsonOptions));
                return defaultConfig;
            }

            var content = File.ReadAllText(ConfigPath);
            using var document = JsonDocument.Parse(content);
            var hasToolAssignments = document.RootElement.TryGetProperty("toolAssignments", out _);
            var hasNeedsAssignmentMigration = document.RootElement.TryGetProperty("needsAssignmentMigration", out _);
            var loaded = JsonSerializer.Deserialize<ToolVisibilityConfig>(content, JsonOptions) ?? CreateDefaultConfig();
            loaded.Mode = NormalizeMode(loaded.Mode);
            loaded.EnabledTools = NormalizeEnabledTools(loaded.EnabledTools);
            loaded.ToolAssignments = hasToolAssignments
                ? NormalizeAssignments(loaded.ToolAssignments, loaded.EnabledTools)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            loaded.NeedsAssignmentMigration = hasNeedsAssignmentMigration
                ? loaded.NeedsAssignmentMigration
                : !hasToolAssignments;
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not load tool visibility config from {ConfigPath}; falling back to mode=all", ConfigPath);
            return CreateDefaultConfig();
        }
    }

    private ToolVisibilitySnapshot BuildSnapshot(ToolVisibilityConfig config, IReadOnlyList<ToolCatalogItem> catalog)
    {
        var tools = catalog
            .Select(tool =>
            {
                var connection = GetAssignedConnection(config, tool.Name);
                var assignment = ToApiAssignment(connection);
                return tool with
                {
                    Enabled = connection is not null,
                    Connection = connection ?? ConnectionNone,
                    Assignment = assignment,
                    Shard = assignment
                };
            })
            .ToArray();

        var groups = tools
            .GroupBy(tool => tool.Group, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var groupTools = group
                    .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new ToolVisibilityGroupSnapshot(
                    group.Key,
                    groupTools.Count(tool => tool.Enabled),
                    groupTools.Length,
                    groupTools);
            })
            .ToArray();

        var connectionCounts = BuildConnectionCounts(tools);

        return new ToolVisibilitySnapshot(
            config.Mode,
            tools.Count(tool => tool.Enabled),
            tools.Length,
            config.EnabledTools,
            config.ToolAssignments,
            connectionCounts,
            DefaultConnectionMaxEnabledTools,
            _externalServerStatuses,
            groups,
            ConfigPath,
            config.UpdatedAtUtc);
    }

    private static ToolCatalogItem BuildCatalogItem(Tool tool, string source)
    {
        var name = NormalizeToolName(tool.Name);
        return new ToolCatalogItem(
            name,
            string.IsNullOrWhiteSpace(tool.Title) ? name : tool.Title!,
            source,
            ResolveGroup(name, source),
            ResolveRisk(name),
            Enabled: true,
            ConnectionNone,
            Assignment: null,
            Shard: null);
    }

    private static bool IsEnabled(ToolVisibilityConfig config, string toolName)
    {
        return GetAssignedConnection(config, toolName) is not null;
    }

    private static string NormalizeMode(string? mode) =>
        string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase) ? "custom" : "all";

    private static bool IsModeCustom(string? mode) =>
        string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase);

    private static string[] NormalizeEnabledTools(IEnumerable<string>? enabledTools) =>
        (enabledTools ?? Array.Empty<string>())
        .Select(NormalizeToolName)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static Dictionary<string, string> NormalizeAssignments(
        IDictionary<string, string>? assignments,
        IEnumerable<string>? legacyEnabledTools)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (assignments is not null)
        {
            foreach (var pair in assignments)
            {
                var name = NormalizeToolName(pair.Key);
                var connection = NormalizeConnection(pair.Value);
                if (!string.IsNullOrWhiteSpace(name) && connection is not null)
                    normalized[name] = connection;
            }
        }

        if (normalized.Count == 0)
        {
            foreach (var name in NormalizeEnabledTools(legacyEnabledTools))
                normalized[name] = ConnectionA;
        }

        return normalized
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string? NormalizeConnection(string? connection)
    {
        if (string.Equals(connection, ConnectionA, StringComparison.OrdinalIgnoreCase))
            return ConnectionA;

        if (string.Equals(connection, ConnectionB, StringComparison.OrdinalIgnoreCase))
            return ConnectionB;

        return null;
    }

    private static string? ToApiAssignment(string? connection) =>
        string.Equals(connection, ConnectionA, StringComparison.Ordinal)
            ? "a"
            : string.Equals(connection, ConnectionB, StringComparison.Ordinal)
                ? "b"
                : null;

    private static string? GetAssignedConnection(ToolVisibilityConfig config, string toolName) =>
        config.ToolAssignments.TryGetValue(toolName, out var connection)
            ? NormalizeConnection(connection)
            : null;

    private static Dictionary<string, int> BuildConnectionCounts(IEnumerable<ToolCatalogItem> tools) =>
        new(StringComparer.Ordinal)
        {
            [ConnectionA] = tools.Count(tool => string.Equals(tool.Connection, ConnectionA, StringComparison.Ordinal)),
            [ConnectionB] = tools.Count(tool => string.Equals(tool.Connection, ConnectionB, StringComparison.Ordinal))
        };

    private static void ValidateConnectionCounts(IReadOnlyDictionary<string, string> assignments)
    {
        var aCount = assignments.Values.Count(connection => string.Equals(connection, ConnectionA, StringComparison.Ordinal));
        var bCount = assignments.Values.Count(connection => string.Equals(connection, ConnectionB, StringComparison.Ordinal));
        if (aCount > DefaultConnectionMaxEnabledTools)
            throw new InvalidOperationException($"Connection A cannot expose more than {DefaultConnectionMaxEnabledTools} enabled tools.");
        if (bCount > DefaultConnectionMaxEnabledTools)
            throw new InvalidOperationException($"Connection B cannot expose more than {DefaultConnectionMaxEnabledTools} enabled tools.");
    }

    private ToolVisibilityConfig MigrateAssignments(ToolVisibilityConfig config, IReadOnlyList<ToolCatalogItem> catalog)
    {
        var enabledNames = IsModeCustom(config.Mode)
            ? NormalizeEnabledTools(config.EnabledTools)
            : catalog.Select(tool => tool.Name).ToArray();

        var orderedEnabledNames = catalog
            .Select(tool => tool.Name)
            .Where(name => enabledNames.Any(enabled => string.Equals(enabled, name, StringComparison.OrdinalIgnoreCase)))
            .Concat(enabledNames.Where(name => catalog.All(tool => !string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in orderedEnabledNames.Take(DefaultConnectionMaxEnabledTools))
            assignments[name] = ConnectionA;

        foreach (var name in orderedEnabledNames.Skip(DefaultConnectionMaxEnabledTools).Take(DefaultConnectionMaxEnabledTools))
            assignments[name] = ConnectionB;

        return new ToolVisibilityConfig
        {
            Mode = "custom",
            EnabledTools = assignments.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
            ToolAssignments = assignments,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            NeedsAssignmentMigration = false
        };
    }

    private void TryPersist(ToolVisibilityConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not persist migrated tool visibility config to {ConfigPath}", ConfigPath);
        }
    }

    private static string NormalizeToolName(string? toolName) =>
        toolName?.Trim() ?? string.Empty;

    private static ToolVisibilityConfig CreateDefaultConfig() => new()
    {
        Mode = "all",
        EnabledTools = Array.Empty<string>(),
        ToolAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        NeedsAssignmentMigration = true
    };

    private static int SourceSortKey(string source) =>
        string.Equals(source, "local", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static string ResolveGroup(string name, string source)
    {
        var lower = name.ToLowerInvariant();

        if (lower is
            "extension_dev_workflow" or
            "browser_extension_inspect" or
            "dom_event_trace" or
            "process_tree_supervisor" or
            "dev_session_run" or
            "visual_regression_compare" or
            "repo_task_checkpoint")
            return "Developer Workflows";

        if (lower.StartsWith("chrome-devtools.", StringComparison.Ordinal) ||
            lower.StartsWith("playwright.", StringComparison.Ordinal) ||
            lower.StartsWith("puppeteer.", StringComparison.Ordinal))
            return "Browser";

        if (lower.StartsWith("context7.", StringComparison.Ordinal))
            return "Docs";

        if (lower.StartsWith("memory.", StringComparison.Ordinal))
            return "Memory";

        if (lower.StartsWith("sequential-thinking.", StringComparison.Ordinal))
            return "Reasoning";

        if (lower.StartsWith("fetch-mcp.", StringComparison.Ordinal))
            return "Web";

        if (lower.StartsWith("obsidian.", StringComparison.Ordinal))
            return "Notes";

        if (lower.StartsWith("fs_", StringComparison.Ordinal) ||
            lower.Contains("file", StringComparison.Ordinal) ||
            lower.Contains("directory", StringComparison.Ordinal))
            return "Files";

        if (lower.StartsWith("github-mcp.", StringComparison.Ordinal))
            return "GitHub";

        if (lower.StartsWith("git_", StringComparison.Ordinal) ||
            lower.StartsWith("git-mcp.", StringComparison.Ordinal))
            return "Git";

        if (lower.StartsWith("powershell", StringComparison.Ordinal) ||
            lower.StartsWith("shell", StringComparison.Ordinal) ||
            lower.StartsWith("process", StringComparison.Ordinal) ||
            lower.StartsWith("app_", StringComparison.Ordinal))
            return "System";

        if (lower.StartsWith("ui_", StringComparison.Ordinal) ||
            lower.StartsWith("window", StringComparison.Ordinal) ||
            lower.StartsWith("screen", StringComparison.Ordinal) ||
            lower.StartsWith("clipboard", StringComparison.Ordinal))
            return "Windows UI";

        if (lower.StartsWith("device", StringComparison.Ordinal) ||
            lower.StartsWith("workspace", StringComparison.Ordinal))
            return "AgentBridge";

        if (lower.Contains("sql", StringComparison.Ordinal) ||
            lower.Contains("database", StringComparison.Ordinal))
            return "Database";

        if (string.Equals(source, "external", StringComparison.OrdinalIgnoreCase))
        {
            var dotIndex = name.IndexOf('.', StringComparison.Ordinal);
            if (dotIndex > 0)
                return name[..dotIndex];
        }

        return "Other";
    }

    private static string ResolveRisk(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower is
            "extension_dev_workflow" or
            "process_tree_supervisor" or
            "dev_session_run" or
            "visual_regression_compare" or
            "repo_task_checkpoint")
            return "dangerous";

        var dangerousFragments = new[]
        {
            "delete",
            "rmdir",
            "move",
            "write",
            "patch",
            "exec",
            "shell",
            "kill",
            "send",
            "update",
            "trash",
            "archive"
        };

        return dangerousFragments.Any(fragment => lower.Contains(fragment, StringComparison.Ordinal))
            ? "dangerous"
            : "safe";
    }
}

public sealed class ToolVisibilityConfig
{
    public string Mode { get; set; } = "all";

    public string[] EnabledTools { get; set; } = Array.Empty<string>();

    public Dictionary<string, string> ToolAssignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool NeedsAssignmentMigration { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ToolVisibilityUpdateRequest
{
    public string? Mode { get; set; }

    public string[]? EnabledTools { get; set; }

    public Dictionary<string, string>? ToolAssignments { get; set; }
}

public sealed record ToolVisibilitySnapshot(
    string Mode,
    int ActiveCount,
    int TotalCount,
    IReadOnlyList<string> EnabledTools,
    IReadOnlyDictionary<string, string> ToolAssignments,
    IReadOnlyDictionary<string, int> ConnectionCounts,
    int MaxEnabledToolsPerConnection,
    IReadOnlyList<ExternalMcpServerCatalogStatus> ExternalServers,
    IReadOnlyList<ToolVisibilityGroupSnapshot> Groups,
    string ConfigPath,
    DateTimeOffset UpdatedAtUtc);

public sealed record ToolVisibilityGroupSnapshot(
    string Name,
    int ActiveCount,
    int TotalCount,
    IReadOnlyList<ToolCatalogItem> Tools);

public sealed record ToolCatalogItem(
    string Name,
    string Title,
    string Source,
    string Group,
    string Risk,
    bool Enabled,
    string Connection,
    string? Assignment,
    string? Shard);
