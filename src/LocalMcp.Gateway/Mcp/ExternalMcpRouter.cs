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
    private readonly Dictionary<string, ExternalMcpServerSession> _sessions;
    private readonly ILogger<ExternalMcpRouter> _logger;
    private ExternalMcpCatalogSnapshot _catalogSnapshot = new(
        Array.Empty<Tool>(),
        Array.Empty<ExternalMcpServerCatalogStatus>());

    public ExternalMcpRouter(
        IOptions<ExternalMcpOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<ExternalMcpRouter> logger)
    {
        _logger = logger;
        _sessions = options.Value.Servers
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value.Enabled)
            .ToDictionary(
                pair => pair.Key.Trim(),
                pair => new ExternalMcpServerSession(pair.Key.Trim(), pair.Value, loggerFactory),
                StringComparer.OrdinalIgnoreCase);

        var initialStatuses = _sessions.Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new ExternalMcpServerCatalogStatus(name, "pending", "External MCP catalog warmup has not finished yet.", 0))
            .ToArray();

        _catalogSnapshot = new ExternalMcpCatalogSnapshot(Array.Empty<Tool>(), initialStatuses);
    }

    public int ServerCount => _sessions.Count;

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
        var entered = await _refreshGate.WaitAsync(RefreshGateImmediateTimeout, cancellationToken);
        if (!entered)
        {
            return GetCatalogSnapshot();
        }
        try
        {
            var results = await Task.WhenAll(_sessions.Values.Select(session => RefreshServerCatalogAsync(session, cancellationToken)));
            var tools = results
                .SelectMany(result => result.Tools)
                .OrderBy(tool => ResolveServerName(tool.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(tool => ResolveToolName(tool.Name), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var statuses = results
                .Select(result => result.Status)
                .OrderBy(status => status.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var snapshot = new ExternalMcpCatalogSnapshot(tools, statuses);

            lock (_catalogGate)
            {
                _catalogSnapshot = snapshot;
            }

            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
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
        var health = new List<ExternalMcpServerHealth>();
        foreach (var session in _sessions.Values)
        {
            health.Add(await session.CheckHealthAsync(cancellationToken));
        }

        var status = health.Count > 0 && health.All(item => item.Status == "ok")
            ? "ok"
            : "degraded";

        return new ExternalMcpHealthReport(status, health);
    }

    public async ValueTask DisposeAsync()
    {
        _refreshGate.Dispose();
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }
    }

    private async Task<ExternalMcpServerCatalogResult> RefreshServerCatalogAsync(
        ExternalMcpServerSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var serverTools = await session.ListToolsAsync(cancellationToken);
            var namespacedTools = serverTools.Select(tool => PrefixTool(session.Name, tool)).ToArray();
            var status = new ExternalMcpServerCatalogStatus(session.Name, "ok", "tools/list succeeded.", namespacedTools.Length);
            _logger.LogInformation("External MCP server {ServerName} tools/list returned {ToolCount} tools", session.Name, namespacedTools.Length);
            return new ExternalMcpServerCatalogResult(namespacedTools, status);
        }
        catch (Exception ex)
        {
            var message = ClassifyFailure(ex);
            var status = new ExternalMcpServerCatalogStatus(session.Name, "error", message, 0);
            _logger.LogWarning(ex, "External MCP server {ServerName} tools/list failed: {Message}", session.Name, message);
            return new ExternalMcpServerCatalogResult(Array.Empty<Tool>(), status);
        }
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
