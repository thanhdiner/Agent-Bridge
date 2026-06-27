using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.Agent.Windows.Commands;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.BuildingBlocks.Errors;
namespace LocalMcp.Agent.Windows.Connection;

public sealed class GatewayConnection : IAsyncDisposable
{
    private readonly AgentOptions _agentOptions;
    private readonly AgentSecurityOptions _agentSecurityOptions;
    private readonly CommandHandler _commandHandler;
    private readonly ILogger<GatewayConnection> _logger;
    private HubConnection? _connection;

    public GatewayConnection(
        IOptions<AgentOptions> agentOptions,
        IOptions<AgentSecurityOptions> agentSecurityOptions,
        CommandHandler commandHandler,
        ILogger<GatewayConnection> logger)
    {
        _agentOptions = agentOptions.Value;
        _agentSecurityOptions = agentSecurityOptions.Value;
        _commandHandler = commandHandler;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_agentOptions.GatewayUrl))
        {
            throw new InvalidOperationException("GatewayUrl is missing or invalid.");
        }

        if (string.IsNullOrWhiteSpace(_agentOptions.DeviceId))
        {
            throw new InvalidOperationException("DeviceId is missing.");
        }

        var hubUrl = $"{_agentOptions.GatewayUrl.TrimEnd('/')}/hubs/agent?deviceId={Uri.EscapeDataString(_agentOptions.DeviceId)}";
        _logger.LogInformation("Initializing SignalR connection to Gateway at {GatewayUrl} for device {DeviceId}", _agentOptions.GatewayUrl, _agentOptions.DeviceId);

        string? agentToken = null;
        if (_agentSecurityOptions.AuthenticationEnabled)
        {
            var envVarName = _agentSecurityOptions.TokenEnvironmentVariable;
            if (!string.IsNullOrWhiteSpace(envVarName))
            {
                agentToken = Environment.GetEnvironmentVariable(envVarName);
            }

            if (string.IsNullOrWhiteSpace(agentToken))
            {
                throw new InvalidOperationException($"Agent authentication is enabled but the token in environment variable '{envVarName}' is missing.");
            }
        }

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (agentToken != null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(agentToken);
                }
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            })
            .Build();

        _connection.Reconnecting += (exception) =>
        {
            _logger.LogWarning(exception, "SignalR connection lost. Reconnecting...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += (connectionId) =>
        {
            _logger.LogInformation("SignalR connection restored. ConnectionId: {ConnectionId}", connectionId);
            return Task.CompletedTask;
        };

        _connection.Closed += (exception) =>
        {
            _logger.LogError(exception, "SignalR connection closed.");
            return Task.CompletedTask;
        };

        _connection.On<JsonElement>("ReceiveCommand", async (json) =>
        {
            Guid commandId = Guid.Empty;
            try
            {
                if (json.TryGetProperty("commandId", out var idProp) && idProp.TryGetGuid(out var parsedId))
                {
                    commandId = parsedId;
                }

                _logger.LogInformation("Received command JSON from Gateway. CommandId: {CommandId}", commandId);

                // Strict: commandType is required
                if (!json.TryGetProperty("commandType", out var typeProp))
                {
                    _logger.LogWarning("Command payload is missing 'commandType' field. CommandId: {CommandId}", commandId);
                    await SendResultAsync(new CommandResult<JsonElement>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.InvalidRequest, "Command payload is missing the required 'commandType' field."),
                        Data = JsonSerializer.SerializeToElement<object?>(null)
                    });
                    return;
                }

                var typeName = typeProp.GetString();
                var rawJson = json.GetRawText();

                AgentCommand? command;
                try
                {
                    command = typeName switch
                    {
                        nameof(ReadFileCommand) => JsonSerializer.Deserialize<ReadFileCommand>(rawJson, JsonOptions.Default),
                        nameof(ReadRangeCommand) => JsonSerializer.Deserialize<ReadRangeCommand>(rawJson, JsonOptions.Default),
                        nameof(ListDirectoryCommand) => JsonSerializer.Deserialize<ListDirectoryCommand>(rawJson, JsonOptions.Default),
                        nameof(SearchFilesCommand) => JsonSerializer.Deserialize<SearchFilesCommand>(rawJson, JsonOptions.Default),
                        nameof(SearchContextCommand) => JsonSerializer.Deserialize<SearchContextCommand>(rawJson, JsonOptions.Default),
                        nameof(GitStatusCommand) => JsonSerializer.Deserialize<GitStatusCommand>(rawJson, JsonOptions.Default),
                        nameof(GitDiffCommand) => JsonSerializer.Deserialize<GitDiffCommand>(rawJson, JsonOptions.Default),
                        nameof(GitLogCommand) => JsonSerializer.Deserialize<GitLogCommand>(rawJson, JsonOptions.Default),
                        nameof(GitShowCommand) => JsonSerializer.Deserialize<GitShowCommand>(rawJson, JsonOptions.Default),
                        nameof(ProjectCheckCommand) => JsonSerializer.Deserialize<ProjectCheckCommand>(rawJson, JsonOptions.Default),
                        nameof(PowerShellExecuteCommand) => JsonSerializer.Deserialize<PowerShellExecuteCommand>(rawJson, JsonOptions.Default),
                        nameof(TreeCommand) => JsonSerializer.Deserialize<TreeCommand>(rawJson, JsonOptions.Default),
                        nameof(WriteFileCommand) => JsonSerializer.Deserialize<WriteFileCommand>(rawJson, JsonOptions.Default),
                        nameof(PatchFileCommand) => JsonSerializer.Deserialize<PatchFileCommand>(rawJson, JsonOptions.Default),
                        nameof(MultiFilePatchCommand) => JsonSerializer.Deserialize<MultiFilePatchCommand>(rawJson, JsonOptions.Default),
                        nameof(CreateDirectoryCommand) => JsonSerializer.Deserialize<CreateDirectoryCommand>(rawJson, JsonOptions.Default),
                        nameof(StatCommand) => JsonSerializer.Deserialize<StatCommand>(rawJson, JsonOptions.Default),
                        nameof(BatchStatCommand) => JsonSerializer.Deserialize<BatchStatCommand>(rawJson, JsonOptions.Default),
                        nameof(BatchReadCommand) => JsonSerializer.Deserialize<BatchReadCommand>(rawJson, JsonOptions.Default),
                        nameof(MoveCommand) => JsonSerializer.Deserialize<MoveCommand>(rawJson, JsonOptions.Default),
                        nameof(CopyCommand) => JsonSerializer.Deserialize<CopyCommand>(rawJson, JsonOptions.Default),
                        nameof(DeleteCommand) => JsonSerializer.Deserialize<DeleteCommand>(rawJson, JsonOptions.Default),
                        nameof(RemoveDirectoryCommand) => JsonSerializer.Deserialize<RemoveDirectoryCommand>(rawJson, JsonOptions.Default),
                        _ => null
                    };
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Malformed JSON in command payload. CommandId: {CommandId}", commandId);
                    await SendResultAsync(new CommandResult<JsonElement>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.InvalidRequest, "Command payload contains malformed JSON."),
                        Data = JsonSerializer.SerializeToElement<object?>(null)
                    });
                    return;
                }

                if (command is null)
                {
                    _logger.LogWarning("Unknown command type '{CommandType}' received. CommandId: {CommandId}", typeName, commandId);
                    await SendResultAsync(new CommandResult<JsonElement>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.UnsupportedCommand, $"Command type '{typeName}' is not supported."),
                        Data = JsonSerializer.SerializeToElement<object?>(null)
                    });
                    return;
                }

                using var cts = new CancellationTokenSource(
                    AgentCommandTimeouts.GetTimeout(command));
                var result = await _commandHandler.HandleAsync(command, cts.Token);

                await SendResultAsync(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing incoming command. CommandId: {CommandId}", commandId);
                var errorResult = new CommandResult<JsonElement>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.InternalError, "Agent internal error processing command."),
                    Data = JsonSerializer.SerializeToElement<object?>(null)
                };
                await SendResultAsync(errorResult);
            }
        });

        await _connection.StartAsync(cancellationToken);
        _logger.LogInformation("SignalR connection started successfully. ConnectionId: {ConnectionId}", _connection.ConnectionId);
    }

    private async Task SendResultAsync(CommandResult<JsonElement> result)
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected)
        {
            _logger.LogWarning("Cannot send result for command {CommandId} because the connection is offline.", result.CommandId);
            return;
        }

        try
        {
            // System.Text.Json cannot serialize an undefined JsonElement. Many failed
            // command results intentionally omit Data, so normalize it to JSON null
            // before crossing the SignalR boundary.
            var transportResult = result.Data.ValueKind == JsonValueKind.Undefined
                ? result with { Data = JsonSerializer.SerializeToElement<object?>(null) }
                : result;

            _logger.LogInformation("Sending result for command {CommandId} back to Gateway (Success={Success})", result.CommandId, result.Success);
            await _connection.InvokeAsync("SubmitResult", transportResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send result for command {CommandId} to Gateway", result.CommandId);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            _logger.LogInformation("Stopping SignalR connection...");
            await _connection.StopAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
