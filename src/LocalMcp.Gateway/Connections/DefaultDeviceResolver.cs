namespace LocalMcp.Gateway.Connections;

public sealed class DefaultDeviceResolver : IDeviceResolver
{
    private readonly IAgentConnectionRegistry _registry;
    private readonly IPreferredDeviceStore _preferredDeviceStore;

    public DefaultDeviceResolver(
        IAgentConnectionRegistry registry,
        IPreferredDeviceStore preferredDeviceStore)
    {
        _registry = registry;
        _preferredDeviceStore = preferredDeviceStore;
    }

    public DeviceResolution Resolve(string? requestedDeviceId)
    {
        if (!string.IsNullOrWhiteSpace(requestedDeviceId))
            return DeviceResolution.Resolved(requestedDeviceId.Trim());

        var preferredDeviceId = _preferredDeviceStore.GetPreferredDeviceId();
        if (!string.IsNullOrWhiteSpace(preferredDeviceId))
        {
            var preferred = preferredDeviceId.Trim();
            return _registry.GetConnectionId(preferred) is not null
                ? DeviceResolution.Resolved(preferred)
                : DeviceResolution.Failed(
                    "PREFERRED_DEVICE_OFFLINE",
                    "The selected default device is offline. Open it or choose another default device.");
        }

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
