namespace LocalMcp.Gateway.Connections;

public sealed class DefaultDeviceResolver : IDeviceResolver
{
    private readonly IAgentConnectionRegistry _registry;

    public DefaultDeviceResolver(IAgentConnectionRegistry registry)
    {
        _registry = registry;
    }

    public DeviceResolution Resolve(string? requestedDeviceId)
    {
        if (!string.IsNullOrWhiteSpace(requestedDeviceId))
            return DeviceResolution.Resolved(requestedDeviceId.Trim());

        var activeDevices = _registry
            .GetActiveDevices()
            .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
            .OrderBy(deviceId => deviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return activeDevices.Length switch
        {
            0 => DeviceResolution.Failed(
                "NO_ACTIVE_DEVICE",
                "No desktop agent is connected. Open AgentBridge Desktop on this computer and try again."),
            1 => DeviceResolution.Resolved(activeDevices[0]),
            _ => DeviceResolution.Failed(
                "MULTIPLE_DEVICES_SELECT_REQUIRED",
                "More than one desktop agent is connected. Select a default device before running this tool.")
        };
    }
}
