using System.Collections.Concurrent;

namespace LocalMcp.Gateway.Connections;

public sealed class InMemoryAgentConnectionRegistry : IAgentConnectionRegistry
{
    private readonly ConcurrentDictionary<string, string> _deviceToConnection = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _connectionToDevice = new();

    public void Register(string deviceId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (_deviceToConnection.TryGetValue(deviceId, out var oldConnId))
        {
            _connectionToDevice.TryRemove(oldConnId, out _);
        }

        _deviceToConnection[deviceId] = connectionId;
        _connectionToDevice[connectionId] = deviceId;
    }

    public void Unregister(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (_connectionToDevice.TryRemove(connectionId, out var deviceId))
        {
            _deviceToConnection.TryRemove(deviceId, out _);
        }
    }

    public string? GetConnectionId(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return _deviceToConnection.TryGetValue(deviceId, out var connectionId) ? connectionId : null;
    }

    public string? GetDeviceId(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return _connectionToDevice.TryGetValue(connectionId, out var deviceId) ? deviceId : null;
    }

    public IReadOnlyCollection<string> GetActiveDevices()
    {
        return _deviceToConnection.Keys.ToList().AsReadOnly();
    }
}
