using System.Text.Json;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace LocalMcp.Agent.AndroidAdb;

public sealed class AndroidGatewayConnection : IAsyncDisposable
{
    private static readonly string[] Capabilities =
    [
        "android.state", "android.screenshot", "android.ui_tree", "android.tap",
        "android.swipe", "android.type_text", "android.press_key", "android.open_app"
    ];

    private readonly AndroidAdbOptions _options;
    private readonly AgentSecurityOptions _securityOptions;
    private readonly AndroidCommandHandler _commandHandler;
    private readonly ILogger<AndroidGatewayConnection> _logger;
    private HubConnection? _connection;

    public AndroidGatewayConnection(
        IOptions<AndroidAdbOptions> options,
        IOptions<AgentSecurityOptions> securityOptions,
        AndroidCommandHandler commandHandler,
        ILogger<AndroidGatewayConnection> logger)
    {
        _options = options.Value;
        _securityOptions = securityOptions.Value;
        _commandHandler = commandHandler;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var identity = await _commandHandler.ProbeAsync(cancellationToken);
        var query = $"deviceId={Uri.EscapeDataString(identity.DeviceId)}" +
            $"&displayName={Uri.EscapeDataString(identity.DisplayName)}" +
            "&platform=android" +
            $"&capabilities={Uri.EscapeDataString(string.Join(',', Capabilities))}";
        var hubUrl = $"{_options.GatewayUrl.TrimEnd('/')}/hubs/agent?{query}";

        string? token = null;
        if (_securityOptions.AuthenticationEnabled)
        {
            token = Environment.GetEnvironmentVariable(_securityOptions.TokenEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException($"Agent token environment variable '{_securityOptions.TokenEnvironmentVariable}' is missing.");
        }

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, httpOptions =>
            {
                if (token is not null)
                    httpOptions.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)])
            .Build();

        _connection.Reconnecting += exception =>
        {
            _logger.LogWarning(exception, "Android agent lost its Gateway connection; reconnecting.");
            return Task.CompletedTask;
        };
        _connection.Reconnected += connectionId =>
        {
            _logger.LogInformation("Android agent reconnected to Gateway as {ConnectionId}.", connectionId);
            return Task.CompletedTask;
        };

        _connection.On<JsonElement>("ReceiveCommand", HandleIncomingCommandAsync);
        await _connection.StartAsync(cancellationToken);
        _logger.LogInformation(
            "Android device {Serial} connected to Gateway as {DeviceId} ({DisplayName}).",
            identity.Serial,
            identity.DeviceId,
            identity.DisplayName);
    }

    private async Task HandleIncomingCommandAsync(JsonElement json)
    {
        var commandId = json.TryGetProperty("commandId", out var idProperty) && idProperty.TryGetGuid(out var parsedId)
            ? parsedId
            : Guid.Empty;
        try
        {
            if (!json.TryGetProperty("commandType", out var typeProperty))
            {
                await SendFailureAsync(commandId, ErrorCodes.InvalidRequest, "Command payload is missing commandType.");
                return;
            }

            var rawJson = json.GetRawText();
            var command = Deserialize(typeProperty.GetString(), rawJson);
            if (command is null)
            {
                await SendFailureAsync(commandId, ErrorCodes.UnsupportedCommand, $"Command type '{typeProperty.GetString()}' is not supported by the Android agent.");
                return;
            }

            using var timeout = new CancellationTokenSource(AgentCommandTimeouts.GetTimeout(command));
            await SendResultAsync(await _commandHandler.HandleAsync(command, timeout.Token));
        }
        catch (JsonException)
        {
            await SendFailureAsync(commandId, ErrorCodes.InvalidRequest, "Command payload contains malformed JSON.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Android command {CommandId}.", commandId);
            await SendFailureAsync(commandId, ErrorCodes.InternalError, "Android agent failed to process the command.");
        }
    }

    private static AgentCommand? Deserialize(string? type, string json) => type switch
    {
        nameof(AndroidGetStateCommand) => JsonSerializer.Deserialize<AndroidGetStateCommand>(json, JsonOptions.Default),
        nameof(AndroidScreenshotCommand) => JsonSerializer.Deserialize<AndroidScreenshotCommand>(json, JsonOptions.Default),
        nameof(AndroidUiTreeCommand) => JsonSerializer.Deserialize<AndroidUiTreeCommand>(json, JsonOptions.Default),
        nameof(AndroidTapCommand) => JsonSerializer.Deserialize<AndroidTapCommand>(json, JsonOptions.Default),
        nameof(AndroidSwipeCommand) => JsonSerializer.Deserialize<AndroidSwipeCommand>(json, JsonOptions.Default),
        nameof(AndroidTypeTextCommand) => JsonSerializer.Deserialize<AndroidTypeTextCommand>(json, JsonOptions.Default),
        nameof(AndroidPressKeyCommand) => JsonSerializer.Deserialize<AndroidPressKeyCommand>(json, JsonOptions.Default),
        nameof(AndroidOpenAppCommand) => JsonSerializer.Deserialize<AndroidOpenAppCommand>(json, JsonOptions.Default),
        _ => null
    };

    private Task SendFailureAsync(Guid commandId, string code, string message) =>
        SendResultAsync(new CommandResult<JsonElement>
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message),
            Data = JsonSerializer.SerializeToElement<object?>(null)
        });

    private async Task SendResultAsync(CommandResult<JsonElement> result)
    {
        if (_connection?.State != HubConnectionState.Connected)
            return;

        var normalized = result.Data.ValueKind == JsonValueKind.Undefined
            ? result with { Data = JsonSerializer.SerializeToElement<object?>(null) }
            : result;
        await _connection.InvokeAsync("SubmitResult", normalized);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
            await _connection.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
