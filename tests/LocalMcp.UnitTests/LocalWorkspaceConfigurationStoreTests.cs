using System.Text.Json.Nodes;
using LocalMcp.BuildingBlocks.Configuration;

namespace LocalMcp.UnitTests;

public sealed class LocalWorkspaceConfigurationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AgentBridgeConfigStoreTests",
        Guid.NewGuid().ToString("N"));

    public LocalWorkspaceConfigurationStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips_Workspaces_And_Roots()
    {
        var mainPath = Path.Combine(_root, "main");
        var readOnlyPath = Path.Combine(_root, "reference");
        Directory.CreateDirectory(mainPath);
        Directory.CreateDirectory(readOnlyPath);
        var store = new LocalWorkspaceConfigurationStore(Path.Combine(_root, "config.json"));

        await store.SaveAsync(
        [
            new WorkspaceConfigurationEntry
            {
                Alias = "main",
                Path = mainPath,
                Writable = true,
                Description = "Primary workspace"
            },
            new WorkspaceConfigurationEntry
            {
                Alias = "reference",
                Path = readOnlyPath,
                Writable = false
            }
        ]);

        var loaded = await store.LoadAsync();
        var root = JsonNode.Parse(await File.ReadAllTextAsync(store.ConfigurationPath))!.AsObject();
        var fileAccess = root["FileAccess"]!.AsObject();

        Assert.Equal(["main", "reference"], loaded.Select(workspace => workspace.Alias));
        Assert.True(loaded[0].Writable);
        Assert.False(loaded[1].Writable);
        Assert.Equal(2, fileAccess["AllowedRoots"]!.AsArray().Count);
        Assert.Single(fileAccess["WritableRoots"]!.AsArray());
    }

    [Fact]
    public async Task Save_Preserves_Unmanaged_Roots_And_Unrelated_Settings()
    {
        var oldManagedPath = Path.Combine(_root, "old");
        var newManagedPath = Path.Combine(_root, "new");
        var manualAllowedPath = Path.Combine(_root, "manual-read");
        var manualWritablePath = Path.Combine(_root, "manual-write");
        foreach (var path in new[]
                 {
                     oldManagedPath,
                     newManagedPath,
                     manualAllowedPath,
                     manualWritablePath
                 })
        {
            Directory.CreateDirectory(path);
        }

        var configurationPath = Path.Combine(_root, "config.json");
        await File.WriteAllTextAsync(
            configurationPath,
            $$"""
              {
                "Security": { "AuthenticationEnabled": true },
                "Workspaces": {
                  "Aliases": {
                    "old": { "Path": "{{Escape(oldManagedPath)}}", "Writable": true }
                  }
                },
                "FileAccess": {
                  "AllowedRoots": [
                    "{{Escape(oldManagedPath)}}",
                    "{{Escape(manualAllowedPath)}}",
                    "{{Escape(manualWritablePath)}}"
                  ],
                  "WritableRoots": [
                    "{{Escape(oldManagedPath)}}",
                    "{{Escape(manualWritablePath)}}"
                  ],
                  "MaxReadBytes": 1234
                }
              }
              """);

        var store = new LocalWorkspaceConfigurationStore(configurationPath);
        await store.SaveAsync(
        [
            new WorkspaceConfigurationEntry
            {
                Alias = "new",
                Path = newManagedPath,
                Writable = true
            }
        ]);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(configurationPath))!.AsObject();
        var fileAccess = root["FileAccess"]!.AsObject();
        var allowed = ReadArray(fileAccess["AllowedRoots"]);
        var writable = ReadArray(fileAccess["WritableRoots"]);

        Assert.True(root["Security"]!["AuthenticationEnabled"]!.GetValue<bool>());
        Assert.Equal(1234, fileAccess["MaxReadBytes"]!.GetValue<int>());
        Assert.DoesNotContain(oldManagedPath, allowed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(newManagedPath, allowed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(manualAllowedPath, allowed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(manualWritablePath, allowed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(oldManagedPath, writable, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(newManagedPath, writable, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(manualWritablePath, writable, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_Rejects_Duplicate_Aliases_Ignoring_Case()
    {
        var store = new LocalWorkspaceConfigurationStore(Path.Combine(_root, "config.json"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
        [
            new WorkspaceConfigurationEntry
            {
                Alias = "main",
                Path = _root,
                Writable = true
            },
            new WorkspaceConfigurationEntry
            {
                Alias = "MAIN",
                Path = _root,
                Writable = false
            }
        ]));

        Assert.Contains("duplicated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static string[] ReadArray(JsonNode? node) =>
        node!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
}
