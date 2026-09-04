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
        if (!string.IsNullOrWhiteSpace(requestedDeviceId) &&
            !IsDefaultDeviceAlias(requestedDeviceId))
        {
            return DeviceResolution.Resolved(requestedDeviceId.Trim());
        }

        var activeDevices = _registry
            .GetActiveDeviceInfos()
            .Where(device => string.Equals(device.Platform, "windows", StringComparison.OrdinalIgnoreCase))
            .Select(device => device.DeviceId)
            .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
            .OrderBy(deviceId => deviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var preferredDeviceId = _preferredDeviceStore.GetPreferredDeviceId();
        if (!string.IsNullOrWhiteSpace(preferredDeviceId))
        {
            var preferred = preferredDeviceId.Trim();
            if (activeDevices.Contains(preferred, StringComparer.OrdinalIgnoreCase))
                return DeviceResolution.Resolved(preferred);

            if (activeDevices.Length == 1)
                return DeviceResolution.Resolved(activeDevices[0]);

            if (activeDevices.Length > 1)
            {
                return DeviceResolution.Failed(
                    "PREFERRED_DEVICE_OFFLINE",
                    "The selected default device is offline and more than one desktop agent is connected. Choose another default device before running this tool.");
            }
        }

        return activeDevices.Length switch
        {
            0 => DeviceResolution.Failed(
                "NO_ACTIVE_DEVICE",
                "No active desktop agent is connected. Open AgentBridge Desktop on this computer and try again."),
            1 => DeviceResolution.Resolved(activeDevices[0]),
            _ => DeviceResolution.Failed(
                "MULTIPLE_DEVICES_SELECT_REQUIRED",
                "More than one desktop agent is connected. Select a default device before running this tool.")
        };
    }

    private static bool IsDefaultDeviceAlias(string requestedDeviceId)
    {
        var normalized = requestedDeviceId.Trim();
        return string.Equals(normalized, "local", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "current", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "active", StringComparison.OrdinalIgnoreCase);
    }
}
