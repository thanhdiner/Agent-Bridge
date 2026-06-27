using LocalMcp.Agent.Windows.Connection;
using LocalMcp.Agent.Windows.PowerShell;
using LocalMcp.Agent.Windows.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalMcp.Agent.Windows;

public sealed class Worker : BackgroundService
{
    private readonly GatewayConnection _gatewayConnection;
    private readonly FileAccessOptions _fileAccessOptions;
    private readonly IPowerShellSessionCoordinator _sessionCoordinator;
    private readonly ILogger<Worker> _logger;

    public Worker(
        GatewayConnection gatewayConnection,
        IOptions<FileAccessOptions> fileAccessOptions,
        IPowerShellSessionCoordinator sessionCoordinator,
        ILogger<Worker> logger)
    {
        _gatewayConnection = gatewayConnection;
        _fileAccessOptions = fileAccessOptions.Value;
        _sessionCoordinator = sessionCoordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Windows Agent Worker Service starting...");
        _logger.LogInformation("Effective AllowedRoots: {AllowedRoots}", FormatRoots(_fileAccessOptions.AllowedRoots));
        _logger.LogInformation("Effective WritableRoots: {WritableRoots}", FormatRoots(_fileAccessOptions.WritableRoots));

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
            // Cancel all active PowerShell sessions before disconnecting to prevent orphaned processes.
            _sessionCoordinator.CancelAll();

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

    private static string FormatRoots(IEnumerable<string>? roots)
    {
        if (roots is null)
        {
            return "(none)";
        }

        var formattedRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root =>
            {
                try
                {
                    return Path.GetFullPath(root);
                }
                catch
                {
                    return $"<invalid:{root}>";
                }
            })
            .ToArray();

        return formattedRoots.Length == 0
            ? "(none)"
            : string.Join("; ", formattedRoots);
    }
}
