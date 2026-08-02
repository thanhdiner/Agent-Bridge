using ModelContextProtocol.Protocol;

namespace LocalMcp.Gateway.Mcp;

public interface IExternalMcpRouter
{
    int ServerCount { get; }

    bool IsExternalToolName(string? toolName);

    Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Tool>> ListToolsAsync(Func<string, bool> includeServer, CancellationToken cancellationToken);

    Task<ExternalMcpCatalogSnapshot> RefreshCatalogAsync(CancellationToken cancellationToken);

    Task<ExternalMcpCatalogSnapshot> WarmupServerAsync(string serverName, CancellationToken cancellationToken);

    Task<ExternalMcpCatalogSnapshot> RestartServerAsync(string serverName, CancellationToken cancellationToken);

    ExternalMcpCatalogSnapshot GetCatalogSnapshot();

    Task<CallToolResult> CallToolAsync(CallToolRequestParams request, CancellationToken cancellationToken);

    Task<ExternalMcpHealthReport> CheckHealthAsync(CancellationToken cancellationToken);
}

public sealed record ExternalMcpHealthReport(
    string Status,
    IReadOnlyList<ExternalMcpServerHealth> Servers);

public sealed record ExternalMcpServerHealth(
    string Name,
    string Status,
    string Message,
    int ToolCount,
    bool PermissionMayBeRequestedAgain);

public sealed record ExternalMcpCatalogSnapshot(
    IReadOnlyList<Tool> Tools,
    IReadOnlyList<ExternalMcpServerCatalogStatus> Servers);

public sealed record ExternalMcpServerCatalogStatus(
    string Name,
    string Status,
    string Message,
    int ToolCount);
