using System.Collections.Concurrent;
using LocalMcp.Gateway;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.Gateway.Connections;
using LocalMcp.Gateway.Hubs;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using Microsoft.Extensions.Logging;

namespace LocalMcp.Gateway.Commands;

public sealed class SignalRCommandDispatcher : ICommandDispatcher
{
    private const int MaxPendingCommands = 1000;
    private readonly IAgentConnectionRegistry _registry;
    private readonly IDeviceResolver _deviceResolver;
    private readonly IDeviceActivationStore _deviceActivationStore;
    private readonly IHubContext<AgentHub> _hubContext;
    private readonly ILogger<SignalRCommandDispatcher> _logger;

    private readonly ConcurrentDictionary<Guid, PendingCommand> _pendingCommands = new();

    public SignalRCommandDispatcher(
        IAgentConnectionRegistry registry,
        IDeviceResolver deviceResolver,
        IDeviceActivationStore deviceActivationStore,
        IHubContext<AgentHub> hubContext,
        ILogger<SignalRCommandDispatcher> logger)
    {
        _registry = registry;
        _deviceResolver = deviceResolver;
        _deviceActivationStore = deviceActivationStore;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<CommandResult<TResult>> SendAsync<TResult>(
        AgentCommand command,
        CancellationToken cancellationToken = default)
    {
        var commandId = command.CommandId;
        var deviceResolution = _deviceResolver.Resolve(command.DeviceId);
        if (!deviceResolution.Success || string.IsNullOrWhiteSpace(deviceResolution.DeviceId))
        {
            return new CommandResult<TResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(
                    deviceResolution.ErrorCode ?? ErrorCodes.InvalidRequest,
                    deviceResolution.ErrorMessage ?? "No active device could be resolved.")
            };
        }

        var deviceId = deviceResolution.DeviceId;
        command = command with { DeviceId = deviceId };

        if (!_deviceActivationStore.IsActivated(deviceId))
        {
            _logger.LogWarning("Device {DeviceId} is not activated, rejecting command {CommandId}", deviceId, commandId);
            return new CommandResult<TResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.DeviceNotActivated, $"Device '{deviceId}' is not activated.")
            };
        }

        _logger.LogInformation("Attempting to dispatch command {CommandId} to device {DeviceId}", commandId, deviceId);

        // 1. Check if agent is online
        var connectionId = _registry.GetConnectionId(deviceId);
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            _logger.LogWarning("Device {DeviceId} is offline, cannot dispatch command {CommandId}", deviceId, commandId);
            return new CommandResult<TResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.AgentOffline, $"Agent '{deviceId}' is offline.")
            };
        }

        // 2. Check capacity limit
        if (_pendingCommands.Count >= MaxPendingCommands)
        {
            _logger.LogWarning("Command capacity limit reached ({MaxLimit}). Rejecting command {CommandId}", MaxPendingCommands, commandId);
            return new CommandResult<TResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCapacityExceeded, "The gateway is at maximum command capacity. Please retry later.")
            };
        }

        // 3. Create TaskCompletionSource
        var tcs = new TaskCompletionSource<CommandResult<JsonElement>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingCommand = new PendingCommand(deviceId, tcs);

        if (!_pendingCommands.TryAdd(commandId, pendingCommand))
        {
            _logger.LogWarning("Duplicate command ID {CommandId} detected in dispatcher", commandId);
            return new CommandResult<TResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InvalidRequest, "Duplicate command ID.")
            };
        }

        var commandTimeout = AgentCommandTimeouts.GetTimeout(command);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(commandTimeout);

        using var registration = cts.Token.Register(() =>
        {
            var isTimeout = !cancellationToken.IsCancellationRequested;
            var code = isTimeout ? ErrorCodes.CommandTimeout : ErrorCodes.CommandCancelled;
            var message = isTimeout
                ? $"The command timed out after {commandTimeout.TotalSeconds:0} seconds."
                : "The command was cancelled by the client.";

            tcs.TrySetResult(new CommandResult<JsonElement>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(code, message),
                Data = JsonSerializer.SerializeToElement<object?>(null)
            });
        });

        try
        {
            // 4. Send command via SignalR to specific client
            await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveCommand", command, cts.Token);
            _logger.LogInformation("Command {CommandId} sent to SignalR client {ConnectionId}", commandId, connectionId);

            // 5. Await result
            var rawResult = await tcs.Task;

            if (!rawResult.Success)
            {
                return new CommandResult<TResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = rawResult.Error
                };
            }

            TResult? deserializedData = default;
            if (rawResult.Data.ValueKind != JsonValueKind.Undefined)
            {
                deserializedData = JsonSerializer.Deserialize<TResult>(rawResult.Data.GetRawText(), JsonOptions.Default);
            }

            return new CommandResult<TResult>
            {
                CommandId = commandId,
                Success = true,
                Data = deserializedData
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing command {CommandId} on device {DeviceId}", commandId, deviceId);
            return new CommandResult<TResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An unexpected gateway error occurred.")
            };
        }
        finally
        {
            _pendingCommands.TryRemove(commandId, out _);
        }
    }

    public void CompleteCommand(Guid commandId, CommandResult<JsonElement> result)
    {
        if (_pendingCommands.TryGetValue(commandId, out var pending))
        {
            if (pending.Tcs.TrySetResult(result))
            {
                _logger.LogInformation("Completed command {CommandId}", commandId);
            }
            else
            {
                _logger.LogWarning("Command {CommandId} was already completed (timeout or cancellation)", commandId);
            }
        }
        else
        {
            _logger.LogWarning("Late response received for command {CommandId} (already timed out or cleaned up)", commandId);
        }
    }

    public void CancelPendingCommandsForDevice(string deviceId)
    {
        var targetCommandIds = _pendingCommands
            .Where(kvp => string.Equals(kvp.Value.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        _logger.LogInformation("Cancelling {Count} pending commands for disconnected device {DeviceId}", targetCommandIds.Count, deviceId);

        foreach (var commandId in targetCommandIds)
        {
            if (_pendingCommands.TryGetValue(commandId, out var pending))
            {
                pending.Tcs.TrySetResult(new CommandResult<JsonElement>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.AgentOffline, "The agent disconnected while the command was pending."),
                    Data = JsonSerializer.SerializeToElement<object?>(null)
                });
            }
        }
    }

    private sealed record PendingCommand(
        string DeviceId,
        TaskCompletionSource<CommandResult<JsonElement>> Tcs
    );
}


