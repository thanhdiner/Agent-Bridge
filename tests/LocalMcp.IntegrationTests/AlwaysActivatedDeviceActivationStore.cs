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
            Activated: true,
            Status: "active",
            ActiveUntilUtc: DateTimeOffset.UtcNow.AddDays(1),
            Features: ["filesystem", "window", "uia", "shell", "git"],
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
    }
}
