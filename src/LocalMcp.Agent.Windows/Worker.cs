using LocalMcp.Agent.Windows.Connection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalMcp.Agent.Windows;

public sealed class Worker : BackgroundService
{
    private readonly GatewayConnection _gatewayConnection;
    private readonly ILogger<Worker> _logger;

    public Worker(GatewayConnection gatewayConnection, ILogger<Worker> logger)
    {
        _gatewayConnection = gatewayConnection;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Windows Agent Worker Service starting...");

        try
        {
            await _gatewayConnection.StartAsync(stoppingToken);
            _logger.LogInformation("Windows Agent connected to Gateway. Running...");

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Windows Agent Worker execution cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error running Windows Agent Worker.");
        }
        finally
        {
            try
            {
                await _gatewayConnection.StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping Gateway connection during shutdown.");
            }
        }

        _logger.LogInformation("Windows Agent Worker Service stopped.");
    }
}
