using System.Text.Json;
using LocalMcp.Gateway;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.Gateway.Connections;
using LocalMcp.Gateway.Commands;
using LocalMcp.Gateway.Hubs;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;

namespace LocalMcp.UnitTests;

public sealed class CommandDispatcherTests
{
    private readonly InMemoryAgentConnectionRegistry _registry;
    private readonly FakeHubContext _fakeHubContext;
    private readonly FakeDeviceActivationStore _activationStore;
    private readonly SignalRCommandDispatcher _dispatcher;

    public CommandDispatcherTests()
    {
        _registry = new InMemoryAgentConnectionRegistry();
        _fakeHubContext = new FakeHubContext();
        _activationStore = new FakeDeviceActivationStore();
        _dispatcher = new SignalRCommandDispatcher(
            _registry,
            new TestDeviceResolver(),
            _activationStore,
            _fakeHubContext,
            NullLogger<SignalRCommandDispatcher>.Instance
        );
    }

    [Fact]
    public async Task SendAsync_AgentOffline_ReturnsAgentOfflineError()
    {
        _activationStore.Activate("offline-device");

        var command = new ReadFileCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "offline-device",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = "test.txt"
        };

        var result = await _dispatcher.SendAsync<ReadFileResult>(command, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCodes.AgentOffline, result.Error.Code);
    }

    [Fact]
    public async Task SendAsync_AgentOnline_DispatchesAndCompletesCommand()
    {
        var deviceId = "online-device";
        var connectionId = "conn-123";
        _registry.Register(deviceId, connectionId);
        _activationStore.Activate(deviceId);

        var command = new ReadFileCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = "test.txt"
        };

        var expectedResultData = new ReadFileResult
        {
            Path = "test.txt",
            Content = "Hello",
            Encoding = "utf-8",
            Size = 5,
            Sha256 = "abc"
        };

        var sendTask = _dispatcher.SendAsync<ReadFileResult>(command, CancellationToken.None);

        Assert.Single(_fakeHubContext.FakeClients.FakeClient.SentMessages);
        var sent = _fakeHubContext.FakeClients.FakeClient.SentMessages[0];
        Assert.Equal("ReceiveCommand", sent.Method);
        Assert.Equal(command, sent.Args[0]);

        var gatewayResult = new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(expectedResultData, JsonOptions.Default)
        };
        _dispatcher.CompleteCommand(command.CommandId, gatewayResult);

        var result = await sendTask;

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(expectedResultData.Content, result.Data.Content);
        Assert.Equal(expectedResultData.Sha256, result.Data.Sha256);
    }

    [Fact]
    public async Task SendAsync_TimeoutOrCancellation_ReturnsTimeoutError()
    {
        var deviceId = "timeout-device";
        var connectionId = "conn-456";
        _registry.Register(deviceId, connectionId);
        _activationStore.Activate(deviceId);

        var command = new ReadFileCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = "test.txt"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        var result = await _dispatcher.SendAsync<ReadFileResult>(command, cts.Token);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.True(result.Error.Code == ErrorCodes.CommandTimeout || result.Error.Code == ErrorCodes.CommandCancelled);
    }

    [Fact]
    public async Task SendAsync_CapacityExceeded_ReturnsCapacityExceededError()
    {
        var deviceId = "capacity-device";
        var connectionId = "conn-789";
        _registry.Register(deviceId, connectionId);
        _activationStore.Activate(deviceId);

        var tasks = new List<Task>();
        for (int i = 0; i < 1000; i++)
        {
            var command = new ReadFileCommand
            {
                CommandId = Guid.NewGuid(),
                DeviceId = deviceId,
                CreatedAt = DateTimeOffset.UtcNow,
                Path = $"test_{i}.txt"
            };
            tasks.Add(_dispatcher.SendAsync<ReadFileResult>(command, CancellationToken.None));
        }

        var extraCommand = new ReadFileCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = "extra.txt"
        };

        var result = await _dispatcher.SendAsync<ReadFileResult>(extraCommand, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCodes.CommandCapacityExceeded, result.Error.Code);
    }

    [Fact]
    public async Task CancelPendingCommandsForDevice_CancelsPendingTasks()
    {
        var deviceId = "disconnect-device";
        var connectionId = "conn-abc";
        _registry.Register(deviceId, connectionId);
        _activationStore.Activate(deviceId);

        var command = new ReadFileCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = "test.txt"
        };

        var sendTask = _dispatcher.SendAsync<ReadFileResult>(command, CancellationToken.None);

        _dispatcher.CancelPendingCommandsForDevice(deviceId);

        var result = await sendTask;

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCodes.AgentOffline, result.Error.Code);
    }
    [Fact]
    public async Task SendAsync_DeviceNotActivated_ReturnsDeviceNotActivatedError()
    {
        var deviceId = "not-activated-device";
        _registry.Register(deviceId, "conn-not-activated");

        var command = new ReadFileCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = "test.txt"
        };

        var result = await _dispatcher.SendAsync<ReadFileResult>(command, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCodes.DeviceNotActivated, result.Error.Code);
        Assert.Empty(_fakeHubContext.FakeClients.FakeClient.SentMessages);
    }

    private sealed class FakeDeviceActivationStore : IDeviceActivationStore
    {
        private readonly HashSet<string> _activatedDeviceIds = new(StringComparer.OrdinalIgnoreCase);

        public void Activate(string deviceId) => _activatedDeviceIds.Add(deviceId);

        public bool IsActivated(string deviceId) =>
            !string.IsNullOrWhiteSpace(deviceId) &&
            _activatedDeviceIds.Contains(deviceId.Trim());

        public DeviceActivationRecord? GetByDeviceId(string deviceId)
        {
            if (IsActivated(deviceId))
            {
                return new DeviceActivationRecord(
                    AccountId: "test-account",
                    DeviceId: deviceId,
                    DeviceName: "Test Device",
                    ActivationToken: "test-token",
                    Plan: "dev",
                    Activated: true,
                    ActivatedAt: DateTimeOffset.UtcNow);
            }
            return null;
        }
    }

    private sealed class TestDeviceResolver : IDeviceResolver
    {
        public DeviceResolution Resolve(string? requestedDeviceId)
        {
            return string.IsNullOrWhiteSpace(requestedDeviceId)
                ? DeviceResolution.Failed("NO_ACTIVE_DEVICE", "No active device.")
                : DeviceResolution.Resolved(requestedDeviceId.Trim());
        }
    }

    private sealed class FakeHubContext : IHubContext<AgentHub>
    {
        public FakeHubClients FakeClients { get; } = new();
        public IHubClients Clients => FakeClients;
        public IGroupManager Groups => throw new NotImplementedException();
    }

    private sealed class FakeHubClients : IHubClients
    {
        public FakeClientProxy FakeClient { get; } = new();

        public IClientProxy All => throw new NotImplementedException();
        public IClientProxy Client(string connectionId) => FakeClient;
        public IClientProxy Group(string groupName) => throw new NotImplementedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotImplementedException();
        public IClientProxy User(string userId) => throw new NotImplementedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotImplementedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotImplementedException();
    }

    private sealed class FakeClientProxy : IClientProxy
    {
        public List<(string Method, object?[] Args)> SentMessages { get; } = new();

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            SentMessages.Add((method, args));
            return Task.CompletedTask;
        }
    }
}



