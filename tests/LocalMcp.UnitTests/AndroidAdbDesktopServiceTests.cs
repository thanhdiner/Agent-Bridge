using AgentBridge.Desktop.Services;

namespace LocalMcp.UnitTests;

public sealed class AndroidAdbDesktopServiceTests
{
    [Fact]
    public void BuildEndpoint_AcceptsIpv4AndValidPort()
    {
        var endpoint = AndroidAdbService.BuildEndpoint("192.168.1.8", 44815);

        Assert.Equal("192.168.1.8:44815", endpoint);
    }

    [Theory]
    [InlineData("phone.local", 44815)]
    [InlineData("192.168.1.8", 0)]
    [InlineData("192.168.1.8", 65536)]
    public void BuildEndpoint_RejectsInvalidValues(string ip, int port)
    {
        Assert.ThrowsAny<ArgumentException>(() => AndroidAdbService.BuildEndpoint(ip, port));
    }

    [Fact]
    public void ParseDevices_ReadsNumericAndMdnsSerials()
    {
        const string output = """
            List of devices attached
            192.168.1.2:38621      device product:ares model:M2012K10C device:ares transport_id:8
            adb-example._adb-tls-connect._tcp device product:ares model:M2012K10C device:ares transport_id:7

            """;

        var devices = AndroidAdbService.ParseDevices(output);

        Assert.Collection(
            devices,
            first =>
            {
                Assert.Equal("192.168.1.2:38621", first.Serial);
                Assert.Equal("device", first.State);
            },
            second => Assert.Equal("adb-example._adb-tls-connect._tcp", second.Serial));
    }

    [Fact]
    public void ParseServices_ReadsPairingAndConnectionPorts()
    {
        const string output = """
            List of discovered mdns services
            adb-one._adb-tls-pairing._tcp. 192.168.1.8:45995
            adb-two._adb-tls-connect._tcp 192.168.1.8:44815
            """;

        var services = AndroidAdbService.ParseServices(output);

        Assert.Collection(
            services,
            pairing =>
            {
                Assert.Equal(AndroidAdbServiceKind.Pairing, pairing.Kind);
                Assert.Equal("192.168.1.8:45995", pairing.Endpoint);
            },
            connection =>
            {
                Assert.Equal(AndroidAdbServiceKind.Connection, connection.Kind);
                Assert.Equal("192.168.1.8:44815", connection.Endpoint);
            });
    }

    [Fact]
    public async Task SettingsStore_DoesNotHaveAPairingCodeField()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentBridge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "android-adb.json");
        try
        {
            var store = new AndroidAdbSettingsStore(path);
            await store.SaveAsync(new AndroidAdbSettings(
                "C:\\Android\\adb.exe",
                "192.168.1.8",
                45995,
                44815));

            var json = await File.ReadAllTextAsync(path);
            var loaded = await store.LoadAsync();

            Assert.DoesNotContain("pairingCode", json, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(45995, loaded.PairingPort);
            Assert.Equal(44815, loaded.ConnectionPort);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
