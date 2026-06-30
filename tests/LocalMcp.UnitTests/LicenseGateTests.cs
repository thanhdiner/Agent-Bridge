using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Gateway;
using LocalMcp.Gateway.Licensing;

namespace LocalMcp.UnitTests;

public sealed class LicenseGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_ActiveLicense_ReturnsAllow()
    {
        var store = new FakeDeviceActivationStore();
        store.Set(CreateRecord(activeUntilUtc: Now.AddDays(1)));
        var gate = new LicenseGate(store, new FixedTimeProvider(Now));

        var decision = gate.Evaluate("test-device");

        Assert.True(decision.Allowed);
        Assert.Null(decision.ErrorCode);
    }

    [Fact]
    public void Evaluate_ExpiredLicense_ReturnsLicenseExpired()
    {
        var store = new FakeDeviceActivationStore();
        store.Set(CreateRecord(activeUntilUtc: Now.AddMinutes(-1)));
        var gate = new LicenseGate(store, new FixedTimeProvider(Now));

        var decision = gate.Evaluate("test-device");

        Assert.False(decision.Allowed);
        Assert.Equal(ErrorCodes.LicenseExpired, decision.ErrorCode);
    }

    [Fact]
    public void Evaluate_RevokedLicense_ReturnsLicenseRevoked()
    {
        var store = new FakeDeviceActivationStore();
        store.Set(CreateRecord(status: "revoked", activeUntilUtc: Now.AddDays(1)));
        var gate = new LicenseGate(store, new FixedTimeProvider(Now));

        var decision = gate.Evaluate("test-device");

        Assert.False(decision.Allowed);
        Assert.Equal(ErrorCodes.LicenseRevoked, decision.ErrorCode);
    }

    [Fact]
    public void Evaluate_MissingActivation_ReturnsDeviceNotActivated()
    {
        var gate = new LicenseGate(new FakeDeviceActivationStore(), new FixedTimeProvider(Now));

        var decision = gate.Evaluate("missing-device");

        Assert.False(decision.Allowed);
        Assert.Equal(ErrorCodes.DeviceNotActivated, decision.ErrorCode);
    }

    [Fact]
    public void Evaluate_MissingActiveUntil_ReturnsLicenseMissing()
    {
        var store = new FakeDeviceActivationStore();
        store.Set(CreateRecord(activeUntilUtc: null));
        var gate = new LicenseGate(store, new FixedTimeProvider(Now));

        var decision = gate.Evaluate("test-device");

        Assert.False(decision.Allowed);
        Assert.Equal(ErrorCodes.LicenseMissing, decision.ErrorCode);
    }

    private static DeviceActivationRecord CreateRecord(
        string status = "active",
        DateTimeOffset? activeUntilUtc = null) =>
        new(
            AccountId: "test-account",
            DeviceId: "test-device",
            DeviceName: "Test Device",
            ActivationToken: "test-token",
            Activated: true,
            Status: status,
            ActiveUntilUtc: activeUntilUtc,
            Features: ["filesystem", "window", "uia", "shell", "git"],
            CreatedAtUtc: Now,
            UpdatedAtUtc: Now);

    private sealed class FakeDeviceActivationStore : IDeviceActivationStore
    {
        private DeviceActivationRecord? _record;

        public void Set(DeviceActivationRecord record) => _record = record;

        public bool IsActivated(string deviceId) => GetByDeviceId(deviceId) is { Activated: true };

        public DeviceActivationRecord? GetByDeviceId(string deviceId) =>
            string.Equals(_record?.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                ? _record
                : null;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
