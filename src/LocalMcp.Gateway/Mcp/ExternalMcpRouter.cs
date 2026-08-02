using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace LocalMcp.Gateway.Mcp;

public sealed class ExternalMcpRouter : IExternalMcpRouter, IAsyncDisposable
{
    private const string ToolPrefixSeparator = ".";
    private static readonly TimeSpan RefreshGateImmediateTimeout = TimeSpan.Zero;
    private readonly object _catalogGate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _serverStartupGate;
    private readonly Dictionary<string, ExternalMcpServerOptions> _serverOptions;
    private readonly Dictionary<string, ExternalMcpServerSession> _sessions;
    private readonly ExternalMcpCatalogCache _catalogCache;
    private readonly ILogger<ExternalMcpRouter> _logger;
    private ExternalMcpCatalogSnapshot _catalogSnapshot = new(
        Array.Empty<Tool>(),
        Array.Empty<ExternalMcpServerCatalogStatus>());

    public ExternalMcpRouter(
        IOptions<ExternalMcpOptions> options,
        ExternalMcpCatalogCache catalogCache,
        ILoggerFactory loggerFactory,
        ILogger<ExternalMcpRouter> logger)
    {
        _logger = logger;
        _catalogCache = catalogCache;
        var externalMcpOptions = options.Value;
        _serverStartupGate = new SemaphoreSlim(Math.Max(1, externalMcpOptions.MaxConcurrentWarmups), Math.Max(1, externalMcpOptions.MaxConcurrentWarmups));
        _serverOptions = options.Value.Servers
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(
                pair => pair.Key.Trim(),
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        _sessions = _serverOptions
            .Where(pair => pair.Value.Enabled)
            .ToDictionary(
                pair => pair.Key,
                pair => new ExternalMcpServerSession(pair.Key, pair.Value, externalMcpOptions.FailureCooldownSeconds, loggerFactory),
                StringComparer.OrdinalIgnoreCase);

        var cachedSnapshot = _catalogCache.Load();
        var cachedTools = cachedSnapshot.Tools
            .Where(tool => _sessions.ContainsKey(ResolveServerName(tool.Name)))
            .ToArray();
        var initialStatuses = BuildStatuses(cachedTools);

        _catalogSnapshot = new ExternalMcpCatalogSnapshot(cachedTools, initialStatuses);
    }

    public int ServerCount => _serverOptions.Count;

    public Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken) =>
        ListToolsAsync(_ => true, cancellationToken);

    public Task<IReadOnlyList<Tool>> ListToolsAsync(Func<string, bool> includeServer, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var snapshot = GetCatalogSnapshot();
        IReadOnlyList<Tool> tools = snapshot.Tools
            .Where(tool => includeServer(ResolveServerName(tool.Name)))
            .ToArray();
        return Task.FromResult(tools);
    }

    public async Task<ExternalMcpCatalogSnapshot> RefreshCatalogAsync(CancellationToken cancellationToken)
    {
        var startupSessions = _sessions.Values
            .Where(session => session.InitializeOnStartup)
            .ToArray();
        if (startupSessions.Length == 0)
        {
            return GetCatalogSnapshot();
        }

        var entered = await _refreshGate.WaitAsync(RefreshGateImmediateTimeout, cancellationToken);
        if (!entered)
        {
            return GetCatalogSnapshot();
        }
        try
        {
            var results = await Task.WhenAll(startupSessions.Select(session => RefreshServerCatalogAsync(session, force: false, cancellationToken)));
            return ApplyCatalogResults(results);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<ExternalMcpCatalogSnapshot> WarmupServerAsync(string serverName, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(serverName.Trim(), out var session))
            throw CreateUnknownOrDisabledServerException(serverName);

        var result = await RefreshServerCatalogAsync(session, force: true, cancellationToken);
        return ApplyCatalogResults([result]);
    }

    public async Task<ExternalMcpCatalogSnapshot> RestartServerAsync(string serverName, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(serverName.Trim(), out var session))
            throw CreateUnknownOrDisabledServerException(serverName);

        await session.RestartAsync(cancellationToken);
        var result = await RefreshServerCatalogAsync(session, force: true, cancellationToken);
        return ApplyCatalogResults([result]);
    }

    public ExternalMcpCatalogSnapshot GetCatalogSnapshot()
    {
        lock (_catalogGate)
        {
            return _catalogSnapshot;
        }
    }

    public bool IsExternalToolName(string? toolName)
    {
        var requestedName = toolName?.Trim() ?? string.Empty;
        var separatorIndex = requestedName.IndexOf(ToolPrefixSeparator, StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == requestedName.Length - 1)
        {
            return false;
        }

        var serverName = requestedName[..separatorIndex];
        return _sessions.ContainsKey(serverName);
    }

    public async Task<CallToolResult> CallToolAsync(CallToolRequestParams request, CancellationToken cancellationToken)
    {
        var requestedName = request.Name?.Trim() ?? string.Empty;
        var separatorIndex = requestedName.IndexOf(ToolPrefixSeparator, StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == requestedName.Length - 1)
        {
            return Error("UNKNOWN_EXTERNAL_TOOL", $"External MCP tool '{requestedName}' must be namespaced as '<server>.<tool>'.");
        }

        var serverName = requestedName[..separatorIndex];
        var toolName = requestedName[(separatorIndex + 1)..];

        if (!_sessions.TryGetValue(serverName, out var session))
        {
            return Error("UNKNOWN_EXTERNAL_SERVER", $"External MCP server '{serverName}' is not configured.");
        }

        var redacted = RedactArgumentsForLog(request.Arguments);
        _logger.LogInformation("Routing external MCP tool call {ToolName} with arguments {Arguments}", requestedName, redacted);

        var forwarded = new CallToolRequestParams
        {
            Name = toolName,
            Arguments = request.Arguments
        };

        return await session.CallToolAsync(forwarded, cancellationToken);
    }

    public async Task<ExternalMcpHealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        _ = cancellationToken;
        var snapshot = GetCatalogSnapshot();
        var health = snapshot.Servers
            .Select(server => new ExternalMcpServerHealth(
                server.Name,
                server.Status,
                server.Message,
                server.ToolCount,
                PermissionMayBeRequestedAgain: false))
            .ToArray();

        var status = health.Any(item => item.Status == "failed")
            ? "degraded"
            : "ok";

        return new ExternalMcpHealthReport(status, health);
    }

    public async ValueTask DisposeAsync()
    {
        _refreshGate.Dispose();
        _serverStartupGate.Dispose();
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }
    }

    private async Task<ExternalMcpServerCatalogResult> RefreshServerCatalogAsync(
        ExternalMcpServerSession session,
        bool force,
        CancellationToken cancellationToken)
    {
        var cachedTools = GetCatalogSnapshot().Tools
            .Where(tool => string.Equals(ResolveServerName(tool.Name), session.Name, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!session.CanRefreshCatalog(DateTimeOffset.UtcNow, force))
        {
            return new ExternalMcpServerCatalogResult(cachedTools, session.GetCatalogStatus(cachedTools.Length));
        }

        SetServerStatus(new ExternalMcpServerCatalogStatus(
            session.Name,
            "discovering",
            "Discovering external MCP tools with tools/list.",
            cachedTools.Length));

        await _serverStartupGate.WaitAsync(cancellationToken);
        try
        {
            var serverTools = await session.ListToolsAsync(cancellationToken);
            var namespacedTools = serverTools.Select(tool => PrefixTool(session.Name, tool)).ToArray();
            session.NoteCatalogSuccess();
            var status = new ExternalMcpServerCatalogStatus(session.Name, "running", "tools/list succeeded.", namespacedTools.Length);
            _logger.LogInformation("External MCP server {ServerName} tools/list returned {ToolCount} tools", session.Name, namespacedTools.Length);
            return new ExternalMcpServerCatalogResult(namespacedTools, status);
        }
        catch (Exception ex)
        {
            var message = ClassifyFailure(ex);
            session.NoteCatalogFailure();
            var status = cachedTools.Length > 0
                ? new ExternalMcpServerCatalogStatus(session.Name, "stale_cached", $"{message} Using cached tools from the last successful tools/list.", cachedTools.Length)
                : new ExternalMcpServerCatalogStatus(session.Name, "failed", message, 0);
            _logger.LogWarning(ex, "External MCP server {ServerName} tools/list failed: {Message}", session.Name, message);
            return new ExternalMcpServerCatalogResult(cachedTools, status);
        }
        finally
        {
            _serverStartupGate.Release();
        }
    }

    private ExternalMcpCatalogSnapshot ApplyCatalogResults(IReadOnlyList<ExternalMcpServerCatalogResult> results)
    {
        ExternalMcpCatalogSnapshot snapshot;
        lock (_catalogGate)
        {
            var refreshedServers = results
                .Select(result => result.Status.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var tools = _catalogSnapshot.Tools
                .Where(tool => !refreshedServers.Contains(ResolveServerName(tool.Name)))
                .Concat(results.SelectMany(result => result.Tools))
                .Where(tool => _sessions.ContainsKey(ResolveServerName(tool.Name)))
                .GroupBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(tool => ResolveServerName(tool.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(tool => ResolveToolName(tool.Name), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var statusesByName = BuildStatuses(tools)
                .ToDictionary(status => status.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var result in results)
                statusesByName[result.Status.Name] = result.Status;

            var statuses = statusesByName.Values
                .OrderBy(status => status.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            snapshot = new ExternalMcpCatalogSnapshot(tools, statuses);
            _catalogSnapshot = snapshot;
        }

        _catalogCache.Save(snapshot);
        return snapshot;
    }

    private ExternalMcpServerCatalogStatus[] BuildStatuses(IReadOnlyList<Tool> tools)
    {
        return _serverOptions
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                var serverName = pair.Key;
                var cachedToolCount = tools.Count(tool =>
                    string.Equals(ResolveServerName(tool.Name), serverName, StringComparison.OrdinalIgnoreCase));
                if (!pair.Value.Enabled)
                    return new ExternalMcpServerCatalogStatus(serverName, "disabled", "External MCP server is disabled in configuration.", 0);

                return _sessions.TryGetValue(serverName, out var session)
                    ? session.GetCatalogStatus(cachedToolCount)
                    : new ExternalMcpServerCatalogStatus(serverName, "failed", "External MCP server is enabled but no session was created.", cachedToolCount);
            })
            .ToArray();
    }

    private void SetServerStatus(ExternalMcpServerCatalogStatus status)
    {
        lock (_catalogGate)
        {
            var statusesByName = _catalogSnapshot.Servers
                .ToDictionary(server => server.Name, StringComparer.OrdinalIgnoreCase);
            statusesByName[status.Name] = status;
            _catalogSnapshot = new ExternalMcpCatalogSnapshot(
                _catalogSnapshot.Tools,
                statusesByName.Values
                    .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        }
    }

    private InvalidOperationException CreateUnknownOrDisabledServerException(string serverName)
    {
        var normalized = serverName.Trim();
        return _serverOptions.TryGetValue(normalized, out var options) && !options.Enabled
            ? new InvalidOperationException($"External MCP server '{normalized}' is disabled.")
            : new InvalidOperationException($"External MCP server '{normalized}' is not configured.");
    }

    private static Tool PrefixTool(string serverName, Tool tool)
    {
        return new Tool
        {
            Name = $"{serverName}{ToolPrefixSeparator}{tool.Name}",
            Title = tool.Title,
            Description = AppendSecurityNote(serverName, tool.Description),
            InputSchema = tool.InputSchema,
            OutputSchema = tool.OutputSchema,
            Annotations = tool.Annotations,
            Icons = tool.Icons,
            Meta = tool.Meta
        };
    }

    private static string ResolveServerName(string? toolName)
    {
        var requestedName = toolName?.Trim() ?? string.Empty;
        var separatorIndex = requestedName.IndexOf(ToolPrefixSeparator, StringComparison.Ordinal);
        return separatorIndex <= 0 ? string.Empty : requestedName[..separatorIndex];
    }

    private static string ResolveToolName(string? toolName)
    {
        var requestedName = toolName?.Trim() ?? string.Empty;
        var separatorIndex = requestedName.IndexOf(ToolPrefixSeparator, StringComparison.Ordinal);
        return separatorIndex < 0 || separatorIndex == requestedName.Length - 1
            ? requestedName
            : requestedName[(separatorIndex + 1)..];
    }

    private static string AppendSecurityNote(string serverName, string? description)
    {
        var warning = string.Equals(serverName, "chrome-devtools", StringComparison.OrdinalIgnoreCase)
            ? "AgentBridge note: this Chrome DevTools mode can access tabs and data in the current Chrome profile."
            : $"AgentBridge note: this external MCP server is namespaced as '{serverName}.*' and can automate browser/app flows exposed by that server.";

        return string.IsNullOrWhiteSpace(description)
            ? warning
            : $"{description.Trim()} {warning}";
    }

    private static string RedactArgumentsForLog(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        var redacted = arguments.ToDictionary(
            pair => pair.Key,
            pair => ShouldRedact(pair.Key) ? "[REDACTED]" : RedactValue(pair.Value),
            StringComparer.OrdinalIgnoreCase);

        return JsonSerializer.Serialize(redacted);
    }

    private static object? RedactValue(object? value)
    {
        if (value is JsonElement element)
        {
            return RedactJsonElement(element);
        }

        return value;
    }

    private static object? RedactJsonElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => ShouldRedact(property.Name) ? "[REDACTED]" : RedactJsonElement(property.Value),
                StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray().Select(RedactJsonElement).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };

    private static bool ShouldRedact(string key) =>
        key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("credential", StringComparison.OrdinalIgnoreCase);

    private static string ClassifyFailure(Exception ex)
    {
        var text = ex.ToString();
        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not recognized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
            return "External MCP server command could not be started. Check the configured command and PATH.";

        if (ex is OperationCanceledException or TimeoutException)
            return "External MCP server tools/list timed out.";

        return string.IsNullOrWhiteSpace(ex.Message)
            ? ex.GetType().Name
            : ex.Message;
    }

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }]
    };

    private sealed record ExternalMcpServerCatalogResult(
        IReadOnlyList<Tool> Tools,
        ExternalMcpServerCatalogStatus Status);
}
