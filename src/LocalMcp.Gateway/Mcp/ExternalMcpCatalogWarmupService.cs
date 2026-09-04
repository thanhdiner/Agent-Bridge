using Microsoft.Extensions.Hosting;

namespace LocalMcp.Gateway.Mcp;

public sealed class ExternalMcpCatalogWarmupService : BackgroundService
{
    private readonly IExternalMcpRouter _router;
    private readonly ILogger<ExternalMcpCatalogWarmupService> _logger;

    public ExternalMcpCatalogWarmupService(
        IExternalMcpRouter router,
        ILogger<ExternalMcpCatalogWarmupService> logger)
    {
        _router = router;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("External MCP catalog warmup starting with {ExternalServerCount} configured servers", _router.ServerCount);

        try
        {
            var snapshot = await _router.RefreshCatalogAsync(stoppingToken);
            foreach (var server in snapshot.Servers)
            {
                _logger.LogInformation(
                    "External MCP server {ServerName}: status={Status}, tools={ToolCount}, message={Message}",
                    server.Name,
                    server.Status,
                    server.ToolCount,
                    server.Message);
            }

            _logger.LogInformation(
                "External MCP catalog warmup finished: servers={ExternalServerCount}, externalTools={ExternalToolCount}",
                snapshot.Servers.Count,
                snapshot.Tools.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External MCP catalog warmup failed.");
        }
    }
}
