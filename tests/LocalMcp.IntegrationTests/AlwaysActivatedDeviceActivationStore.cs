using LocalMcp.Gateway;

namespace LocalMcp.IntegrationTests;

internal sealed class AlwaysActivatedDeviceActivationStore : IDeviceActivationStore
{
    public bool IsActivated(string deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId);

    public DeviceActivationRecord? GetByDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        return new DeviceActivationRecord(
            AccountId: "always-activated",
            DeviceId: deviceId,
            DeviceName: "Always Activated",
            ActivationToken: "always-activated-token",
            Plan: "dev",
            Activated: true,
            ActivatedAt: DateTimeOffset.UtcNow);
    }
}
