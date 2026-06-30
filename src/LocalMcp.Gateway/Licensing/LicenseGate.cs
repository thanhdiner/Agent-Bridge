using LocalMcp.BuildingBlocks.Errors;

namespace LocalMcp.Gateway.Licensing;

public sealed class LicenseGate : ILicenseGate
{
    private readonly IDeviceActivationStore _activationStore;
    private readonly TimeProvider _timeProvider;

    public LicenseGate(IDeviceActivationStore activationStore)
        : this(activationStore, TimeProvider.System)
    {
    }

    public LicenseGate(IDeviceActivationStore activationStore, TimeProvider timeProvider)
    {
        _activationStore = activationStore;
        _timeProvider = timeProvider;
    }

    public LicenseDecision Evaluate(string deviceId)
    {
        var activation = _activationStore.GetByDeviceId(deviceId);
        if (activation is not { Activated: true })
        {
            return LicenseDecision.Deny(
                ErrorCodes.DeviceNotActivated,
                $"Device '{deviceId}' is not activated.");
        }

        if (string.Equals(activation.Status, "revoked", StringComparison.OrdinalIgnoreCase))
        {
            return LicenseDecision.Deny(
                ErrorCodes.LicenseRevoked,
                "The license has been revoked.");
        }

        if (activation.ActiveUntilUtc is null)
        {
            return LicenseDecision.Deny(
                ErrorCodes.LicenseMissing,
                "No active license period is available.");
        }

        if (activation.ActiveUntilUtc <= _timeProvider.GetUtcNow())
        {
            return LicenseDecision.Deny(
                ErrorCodes.LicenseExpired,
                "The license has expired.");
        }

        return LicenseDecision.Allow();
    }
}
