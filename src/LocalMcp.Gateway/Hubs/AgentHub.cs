using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using LocalMcp.Contracts.Results;
using LocalMcp.Gateway.Connections;
using LocalMcp.Gateway.Commands;
using Microsoft.Extensions.Logging;

namespace LocalMcp.Gateway.Hubs;

public sealed class AgentHub : Hub
{
    private readonly IAgentConnectionRegistry _registry;
    private readonly ICommandDispatcher _dispatcher;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(
        IAgentConnectionRegistry registry,
        ICommandDispatcher dispatcher,
        ILogger<AgentHub> logger)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var deviceId = httpContext?.Request.Query["deviceId"].ToString();
        var displayName = httpContext?.Request.Query["displayName"].ToString();
        var platform = httpContext?.Request.Query["platform"].ToString();
        var capabilities = httpContext?.Request.Query["capabilities"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            _logger.LogWarning("Connection attempt rejected: deviceId query parameter is missing. ConnectionId: {ConnectionId}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        _logger.LogInformation(
            "Agent connected: DeviceId = {DeviceId}, DisplayName = {DisplayName}, Platform = {Platform}, ConnectionId = {ConnectionId}",
            deviceId,
            displayName,
            platform,
            Context.ConnectionId);
        _registry.Register(deviceId, Context.ConnectionId, displayName, platform, capabilities);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var deviceId = _registry.GetDeviceId(Context.ConnectionId);
        if (deviceId is not null)
        {
            _logger.LogInformation("Agent disconnected: DeviceId = {DeviceId}, ConnectionId = {ConnectionId}", deviceId, Context.ConnectionId);
            _registry.Unregister(Context.ConnectionId);
            _dispatcher.CancelPendingCommandsForDevice(deviceId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public void SubmitResult(CommandResult<JsonElement> result)
    {
        if (result is null)
        {
            _logger.LogWarning("Received null result submission from connection {ConnectionId}", Context.ConnectionId);
            return;
        }

        var deviceId = _registry.GetDeviceId(Context.ConnectionId);
        _logger.LogInformation("Received result for command {CommandId} from device {DeviceId}", result.CommandId, deviceId);

        _dispatcher.CompleteCommand(result.CommandId, result);
    }
}
