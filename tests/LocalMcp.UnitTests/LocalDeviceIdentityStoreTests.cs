using System.Text.Json;
using LocalMcp.BuildingBlocks.Configuration;

namespace LocalMcp.UnitTests;

public sealed class LocalDeviceIdentityStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AgentBridgeDeviceIdentityTests",
        Guid.NewGuid().ToString("N"));

    public LocalDeviceIdentityStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task LoadOrCreate_Returns_The_Same_Identity_Across_Runs()
    {
        var path = Path.Combine(_root, "device.json");
        var firstStore = new LocalDeviceIdentityStore(path);
        var secondStore = new LocalDeviceIdentityStore(path);

        var first = await firstStore.LoadOrCreateAsync();
        var second = await secondStore.LoadOrCreateAsync();

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.StartsWith("device-", first.DeviceId, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task LoadOrCreate_Writes_A_Readable_Device_Document()
    {
        var path = Path.Combine(_root, "device.json");
        var store = new LocalDeviceIdentityStore(path);

        var identity = await store.LoadOrCreateAsync();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        Assert.Equal(identity.DeviceId, document.RootElement.GetProperty("deviceId").GetString());
        Assert.True(document.RootElement.TryGetProperty("createdAtUtc", out _));
    }

    [Fact]
    public async Task LoadOrCreate_Rejects_Invalid_Existing_Identity()
    {
        var path = Path.Combine(_root, "device.json");
        await File.WriteAllTextAsync(
            path,
            "{\"deviceId\":\"\",\"createdAtUtc\":\"2026-01-01T00:00:00Z\"}");
        var store = new LocalDeviceIdentityStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadOrCreateAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
