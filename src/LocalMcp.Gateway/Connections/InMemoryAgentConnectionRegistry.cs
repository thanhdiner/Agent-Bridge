using System.Collections.Concurrent;

namespace LocalMcp.Gateway.Connections;

public sealed class InMemoryAgentConnectionRegistry : IAgentConnectionRegistry
{
    private readonly ConcurrentDictionary<string, AgentDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _connectionToDevice = new();

    public void Register(string deviceId, string connectionId, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (_devices.TryGetValue(deviceId, out var oldDevice))
        {
            _connectionToDevice.TryRemove(oldDevice.ConnectionId, out _);
        }

        _devices[deviceId] = new AgentDeviceInfo(
            deviceId,
            NormalizeDisplayName(displayName),
            connectionId,
            DateTimeOffset.UtcNow);
        _connectionToDevice[connectionId] = deviceId;
    }

    public void Unregister(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (_connectionToDevice.TryRemove(connectionId, out var deviceId))
        {
            _devices.TryRemove(deviceId, out _);
        }
    }

    public string? GetConnectionId(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return _devices.TryGetValue(deviceId, out var device) ? device.ConnectionId : null;
    }

    public string? GetDeviceId(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return _connectionToDevice.TryGetValue(connectionId, out var deviceId) ? deviceId : null;
    }

    public AgentDeviceInfo? GetDevice(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return _devices.TryGetValue(deviceId, out var device) ? device : null;
    }

    public IReadOnlyCollection<string> GetActiveDevices()
    {
        return _devices.Keys.ToList().AsReadOnly();
    }

    public IReadOnlyCollection<AgentDeviceInfo> GetActiveDeviceInfos()
    {
        return _devices.Values.ToList().AsReadOnly();
    }

    private static string? NormalizeDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        var normalized = displayName.Trim();
        return normalized.Length > 128 || normalized.Any(char.IsControl)
            ? null
            : normalized;
    }
}
