using LocalMcp.Gateway.Connections;

namespace LocalMcp.UnitTests;

public sealed class DefaultDeviceResolverTests
{
    private readonly InMemoryAgentConnectionRegistry _registry = new();
    private readonly TestPreferredDeviceStore _preferredDeviceStore = new();

    [Fact]
    public void Resolve_MissingDeviceId_UsesOnlyActiveDevice()
    {
        _registry.Register("device-one", "conn-one");
        var resolver = CreateResolver();

        var result = resolver.Resolve(null);

        Assert.True(result.Success);
        Assert.Equal("device-one", result.DeviceId);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("current")]
    [InlineData("default")]
    [InlineData("active")]
    public void Resolve_DefaultAlias_UsesOnlyActiveDevice(string alias)
    {
        _registry.Register("device-one", "conn-one");
        var resolver = CreateResolver();

        var result = resolver.Resolve(alias);

        Assert.True(result.Success);
        Assert.Equal("device-one", result.DeviceId);
    }

    [Fact]
    public void Resolve_PreferredDeviceOnline_UsesPreferredDevice()
    {
        _registry.Register("device-one", "conn-one");
        _registry.Register("device-two", "conn-two");
        _preferredDeviceStore.SetPreferredDeviceId("device-two");
        var resolver = CreateResolver();

        var result = resolver.Resolve(null);

        Assert.True(result.Success);
        Assert.Equal("device-two", result.DeviceId);
    }

    [Fact]
    public void Resolve_PreferredDeviceOfflineWithOneActiveDevice_UsesActiveDevice()
    {
        _registry.Register("device-one", "conn-one");
        _preferredDeviceStore.SetPreferredDeviceId("offline-device");
        var resolver = CreateResolver();

        var result = resolver.Resolve(null);

        Assert.True(result.Success);
        Assert.Equal("device-one", result.DeviceId);
    }

    [Fact]
    public void Resolve_NoActiveDevice_ReturnsClearError()
    {
        var resolver = CreateResolver();

        var result = resolver.Resolve(null);

        Assert.False(result.Success);
        Assert.Equal("NO_ACTIVE_DEVICE", result.ErrorCode);
        Assert.Contains("No active desktop agent", result.ErrorMessage);
    }

    [Fact]
    public void Resolve_MultipleActiveDevicesWithoutPreferred_ReturnsSelectionError()
    {
        _registry.Register("device-one", "conn-one");
        _registry.Register("device-two", "conn-two");
        var resolver = CreateResolver();

        var result = resolver.Resolve(null);

        Assert.False(result.Success);
        Assert.Equal("MULTIPLE_DEVICES_SELECT_REQUIRED", result.ErrorCode);
    }

    [Fact]
    public void Resolve_ExplicitDeviceId_StillUsesRequestedDevice()
    {
        _registry.Register("device-one", "conn-one");
        var resolver = CreateResolver();

        var result = resolver.Resolve(" explicit-device ");

        Assert.True(result.Success);
        Assert.Equal("explicit-device", result.DeviceId);
    }

    private DefaultDeviceResolver CreateResolver() =>
        new(_registry, _preferredDeviceStore);

    private sealed class TestPreferredDeviceStore : IPreferredDeviceStore
    {
        private string? _deviceId;

        public string? GetPreferredDeviceId() => _deviceId;

        public void SetPreferredDeviceId(string deviceId) => _deviceId = deviceId;

        public void ClearPreferredDeviceId() => _deviceId = null;
    }
}
