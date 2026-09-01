namespace LocalMcp.Agent.AndroidAdb;

public sealed class Worker : BackgroundService
{
    private readonly AndroidGatewayConnection _gatewayConnection;
    private readonly ILogger<Worker> _logger;

    public Worker(AndroidGatewayConnection gatewayConnection, ILogger<Worker> logger)
    {
        _gatewayConnection = gatewayConnection;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Android ADB agent starting. It runs independently from the Windows agent.");
        try
        {
            await _gatewayConnection.StartAsync(stoppingToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Android ADB agent stopping.");
        }
        finally
        {
            await _gatewayConnection.StopAsync(CancellationToken.None);
        }
    }
}
