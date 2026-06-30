using LocalMcp.Gateway;

namespace LocalMcp.IntegrationTests;

internal sealed class AlwaysActivatedDeviceActivationStore : IDeviceActivationStore
{
    public bool IsActivated(string deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId);
}
