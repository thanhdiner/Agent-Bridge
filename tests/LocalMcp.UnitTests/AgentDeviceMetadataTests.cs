using LocalMcp.Gateway.Connections;

namespace LocalMcp.UnitTests;

public sealed class AgentDeviceMetadataTests
{
    [Fact]
    public void Registry_NormalizesAndroidPlatformAndCapabilities()
    {
        var registry = new InMemoryAgentConnectionRegistry();

        registry.Register(
            "android-phone",
            "connection-1",
            "Pixel",
            "ANDROID",
            ["android.tap", " android.screenshot ", "android.tap", "bad capability!"]);

        var device = Assert.IsType<AgentDeviceInfo>(registry.GetDevice("android-phone"));
        Assert.Equal("android", device.Platform);
        Assert.Equal(["android.screenshot", "android.tap"], device.Capabilities);
    }

    [Fact]
    public void Registry_OldAgentRegistrationDefaultsToWindows()
    {
        var registry = new InMemoryAgentConnectionRegistry();

        registry.Register("desktop", "connection-1");

        Assert.Equal("windows", registry.GetDevice("desktop")!.Platform);
    }
}
