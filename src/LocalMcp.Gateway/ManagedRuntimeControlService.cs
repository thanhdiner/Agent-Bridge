using Microsoft.Extensions.Hosting;

namespace LocalMcp.Gateway;

public sealed class ManagedRuntimeControlService : BackgroundService
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<ManagedRuntimeControlService> _logger;

    public ManagedRuntimeControlService(
        IHostApplicationLifetime applicationLifetime,
        ILogger<ManagedRuntimeControlService> logger)
    {
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("AGENTBRIDGE_MANAGED_RUNTIME"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        _logger.LogInformation("Managed runtime control channel is active.");

        while (!stoppingToken.IsCancellationRequested)
        {
            string? command;
            try
            {
                command = await Console.In.ReadLineAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (command is null)
                return;

            if (!string.Equals(command.Trim(), "stop", StringComparison.OrdinalIgnoreCase))
                continue;

            _logger.LogInformation("Graceful stop requested by AgentBridge Desktop.");
            _applicationLifetime.StopApplication();
            return;
        }
    }
}
