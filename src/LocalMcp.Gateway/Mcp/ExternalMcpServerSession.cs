using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LocalMcp.Gateway.Mcp;

public sealed class ExternalMcpServerSession : IAsyncDisposable
{
    private readonly ExternalMcpServerOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private IReadOnlyList<Tool>? _cachedTools;
    private bool _restartMayNeedPermission;
    private int _consecutiveCatalogFailures;
    private DateTimeOffset? _catalogCooldownUntilUtc;

    public ExternalMcpServerSession(
        string name,
        ExternalMcpServerOptions options,
        int failureCooldownSeconds,
        ILoggerFactory loggerFactory)
    {
        Name = name;
        _options = options;
        FailureCooldownSeconds = Math.Max(1, failureCooldownSeconds);
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ExternalMcpServerSession>();
    }

    public string Name { get; }

    public bool InitializeOnStartup => _options.InitializeOnStartup;

    public int FailureCooldownSeconds { get; }

    public bool IsRunning => _client is not null && _cachedTools is not null;

    public bool CanRefreshCatalog(DateTimeOffset nowUtc, bool force) =>
        force ||
        _catalogCooldownUntilUtc is null ||
        _catalogCooldownUntilUtc <= nowUtc;

    public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return _cachedTools!;
    }

    public async Task<CallToolResult> CallToolAsync(CallToolRequestParams request, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        try
        {
            using var timeout = CreateTimeout(_options.ToolCallTimeoutSeconds, cancellationToken);
            return await _client!.CallToolAsync(request, timeout.Token);
        }
        catch (ClientTransportClosedException ex)
        {
            await ResetAfterCrashAsync(ex, cancellationToken);
            await EnsureInitializedAsync(cancellationToken);
            return await _client!.CallToolAsync(request, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            return Error("MCP_TOOL_TIMEOUT", $"{Name} tool call timed out: {ex.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error("MCP_TOOL_TIMEOUT", $"{Name} tool call timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{ServerName} tool call failed", Name);
            return Error("MCP_TOOL_FAILED", ClassifyFailure(ex));
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _restartMayNeedPermission = true;
            _consecutiveCatalogFailures = 0;
            _catalogCooldownUntilUtc = null;
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }

            _client = null;
            _cachedTools = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ExternalMcpServerCatalogStatus GetCatalogStatus(int cachedToolCount)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        if (IsRunning)
            return new ExternalMcpServerCatalogStatus(Name, "running", "External MCP server is running.", cachedToolCount);

        if (_catalogCooldownUntilUtc is not null && _catalogCooldownUntilUtc > nowUtc)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((_catalogCooldownUntilUtc.Value - nowUtc).TotalSeconds));
            return cachedToolCount > 0
                ? new ExternalMcpServerCatalogStatus(Name, "stale_cached", $"tools/list failed; using cached catalog. Retry available in {seconds}s.", cachedToolCount)
                : new ExternalMcpServerCatalogStatus(Name, "failed", $"External MCP server is in cooldown for {seconds}s.", 0);
        }

        return cachedToolCount > 0
            ? new ExternalMcpServerCatalogStatus(Name, "stale_cached", "Using cached external MCP catalog; server has not been started in this gateway process.", cachedToolCount)
            : new ExternalMcpServerCatalogStatus(Name, "not_discovered", "External MCP server is configured but tools/list has not succeeded yet.", 0);
    }

    public void NoteCatalogSuccess()
    {
        _consecutiveCatalogFailures = 0;
        _catalogCooldownUntilUtc = null;
    }

    public void NoteCatalogFailure()
    {
        _consecutiveCatalogFailures++;
        _catalogCooldownUntilUtc = DateTimeOffset.UtcNow.AddSeconds(FailureCooldownSeconds);
    }

    public async Task<ExternalMcpServerHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("node"))
        {
            return Health("missing_node", "Node.js is not available on PATH.", 0);
        }

        if (!CommandExists("npx"))
        {
            return Health("missing_npx", "npx is not available on PATH.", 0);
        }

        try
        {
            var tools = await ListToolsAsync(cancellationToken);
            if (string.Equals(Name, "chrome-devtools", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await ProbeChromeAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    return Health("error", ClassifyFailure(ex), tools.Count);
                }
            }

            return Health("ok", "MCP initialize, list_tools, and Chrome page probe succeeded.", tools.Count);
        }
        catch (Exception ex)
        {
            return Health("error", ClassifyFailure(ex), 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
                _client = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_client is not null && _cachedTools is not null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null && _cachedTools is not null)
            {
                return;
            }

            ValidateConfiguration();

            using var timeout = CreateTimeout(_options.InitializeTimeoutSeconds, cancellationToken);
            var transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = Name,
                    Command = _options.Command,
                    Arguments = _options.Args,
                    WorkingDirectory = _options.WorkingDirectory,
                    StandardErrorLines = line => _logger.LogInformation("{ServerName} stderr: {Line}", Name, RedactLine(line))
                },
                _loggerFactory);

            _logger.LogInformation(
                "Starting external MCP server {ServerName}: {Command} {Args}. Chrome may ask permission after a reconnect.",
                Name,
                _options.Command,
                string.Join(" ", _options.Args));

            _client = await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: timeout.Token);
            var tools = await _client.ListToolsAsync(cancellationToken: timeout.Token);
            _cachedTools = tools.Select(tool => tool.ProtocolTool).ToArray();

            _logger.LogInformation("External MCP server {ServerName} initialized with {ToolCount} tools", Name, _cachedTools.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetAfterCrashAsync(Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogWarning(exception, "External MCP server {ServerName} disconnected. Restarting; Chrome may ask permission again.", Name);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _restartMayNeedPermission = true;
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }

            _client = null;
            _cachedTools = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.Command))
        {
            throw new InvalidOperationException($"External MCP server '{Name}' has no command configured.");
        }
    }

    private async Task ProbeChromeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(_options.ToolCallTimeoutSeconds, cancellationToken);
        var result = await _client!.CallToolAsync(
            new CallToolRequestParams
            {
                Name = "list_pages",
                Arguments = new Dictionary<string, System.Text.Json.JsonElement>()
            },
            timeout.Token);

        if (result.IsError == true)
        {
            var text = string.Join(
                Environment.NewLine,
                result.Content.OfType<TextContentBlock>().Select(block => block.Text));

            throw new InvalidOperationException(text);
        }
    }

    private static CancellationTokenSource CreateTimeout(int timeoutSeconds, CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        return timeout;
    }

    private ExternalMcpServerHealth Health(string status, string message, int toolCount) =>
        new(Name, status, message, toolCount, _restartMayNeedPermission);

    private static bool CommandExists(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd",
                ArgumentList = { "/c", "where", command },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ClassifyFailure(Exception ex)
    {
        var text = ex.ToString();

        if (text.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Chrome", StringComparison.OrdinalIgnoreCase) && text.Contains("running", StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome does not appear to be running with remote debugging available for the current profile.";
        }

        if (text.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome remote debugging permission was denied or not allowed. Allow the prompt in Chrome and try again.";
        }

        if (text.Contains("remote debugging", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("inspect", StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome remote debugging is not enabled for this browser instance. Open chrome://inspect/#remote-debugging and enable it.";
        }

        if (ex is ClientTransportClosedException)
        {
            return "The MCP server process crashed or closed. It will be restarted on the next Chrome tool call; Chrome may ask permission again.";
        }

        if (ex is OperationCanceledException)
        {
            return "MCP initialize or list_tools timed out. Check Node/npx, Chrome status, remote debugging permission, and the Chrome permission dialog.";
        }

        return ex.Message;
    }

    private static string RedactLine(string line)
    {
        var redacted = line;
        foreach (var key in new[] { "token", "cookie", "password", "secret", "authorization", "credential" })
        {
            redacted = System.Text.RegularExpressions.Regex.Replace(
                redacted,
                $@"({key}\s*[:=]\s*)\S+",
                "$1[REDACTED]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return redacted;
    }

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }]
    };
}
