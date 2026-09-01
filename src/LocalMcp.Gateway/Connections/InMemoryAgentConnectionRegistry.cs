using System.Collections.Concurrent;

namespace LocalMcp.Gateway.Connections;

public sealed class InMemoryAgentConnectionRegistry : IAgentConnectionRegistry
{
    private readonly ConcurrentDictionary<string, AgentDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _connectionToDevice = new();

    public void Register(
        string deviceId,
        string connectionId,
        string? displayName = null,
        string? platform = null,
        IReadOnlyCollection<string>? capabilities = null)
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
            DateTimeOffset.UtcNow,
            NormalizePlatform(platform),
            NormalizeCapabilities(capabilities));
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

    private static string NormalizePlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            return "windows";

        var normalized = platform.Trim().ToLowerInvariant();
        return normalized.Length <= 32
            && normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                ? normalized
                : "unknown";
    }

    private static IReadOnlyList<string> NormalizeCapabilities(IReadOnlyCollection<string>? capabilities)
    {
        if (capabilities is null)
            return Array.Empty<string>();

        return capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Select(capability => capability.Trim().ToLowerInvariant())
            .Where(capability => capability.Length <= 64
                && capability.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(capability => capability, StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .ToArray();
    }
}
