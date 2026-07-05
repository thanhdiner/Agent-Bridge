using LocalMcp.Gateway.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using NSubstitute;

namespace LocalMcp.UnitTests;

public sealed class McpShardToolVisibilityTests
{
    [Fact]
    public void ExternalToolsAppearInToolVisibilityCatalogWithServerStatus()
    {
        using var temp = new TempToolVisibilityConfig();
        var store = temp.CreateStore();

        store.RememberCatalog(
            [Tool("window_list")],
            [Tool("playwright.browser_navigate"), Tool("context7.resolve-library-id")],
            [
                new ExternalMcpServerCatalogStatus("playwright", "ok", "tools/list succeeded.", 1),
                new ExternalMcpServerCatalogStatus("obsidian", "error", "command failed", 0)
            ]);

        var snapshot = store.GetSnapshot();
        var tools = snapshot.Groups.SelectMany(group => group.Tools).ToArray();

        Assert.Contains(tools, tool => tool.Name == "window_list" && tool.Source == "local");
        Assert.Contains(tools, tool => tool.Name == "playwright.browser_navigate" && tool.Source == "external");
        Assert.Contains(tools, tool => tool.Name == "context7.resolve-library-id" && tool.Source == "external");
        Assert.Contains(snapshot.ExternalServers, server => server.Name == "obsidian" && server.Status == "error" && server.ToolCount == 0);
    }

    [Fact]
    public async Task SnapshotToolsExposeSourceAndShardMetadata()
    {
        using var temp = new TempToolVisibilityConfig();
        var store = temp.CreateStore();
        store.RememberCatalog(
            [Tool("window_list")],
            [Tool("playwright.browser_navigate"), Tool("context7.resolve-library-id")],
            []);

        await store.SaveAsync(new ToolVisibilityUpdateRequest
        {
            Mode = "custom",
            ToolAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["window_list"] = ToolVisibilityStore.ConnectionA,
                ["playwright.browser_navigate"] = ToolVisibilityStore.ConnectionB
            }
        });

        var tools = store.GetSnapshot().Groups.SelectMany(group => group.Tools).ToArray();
        var local = Assert.Single(tools, tool => tool.Name == "window_list");
        var externalB = Assert.Single(tools, tool => tool.Name == "playwright.browser_navigate");
        var unassigned = Assert.Single(tools, tool => tool.Name == "context7.resolve-library-id");

        Assert.Equal("local", local.Source);
        Assert.Equal("a", local.Assignment);
        Assert.Equal("a", local.Shard);
        Assert.Equal(ToolVisibilityStore.ConnectionA, local.Connection);

        Assert.Equal("external", externalB.Source);
        Assert.Equal("b", externalB.Assignment);
        Assert.Equal("b", externalB.Shard);
        Assert.Equal(ToolVisibilityStore.ConnectionB, externalB.Connection);

        Assert.Equal("external", unassigned.Source);
        Assert.Null(unassigned.Assignment);
        Assert.Null(unassigned.Shard);
        Assert.Equal(ToolVisibilityStore.ConnectionNone, unassigned.Connection);
    }

    [Fact]
    public async Task ExternalToolsCanBeAssignedToShardAAndB()
    {
        using var temp = new TempToolVisibilityConfig();
        var store = temp.CreateStore();
        var externalTools = new[]
        {
            Tool("playwright.browser_navigate"),
            Tool("github-mcp.create_issue")
        };
        store.RememberCatalog([], externalTools, [new ExternalMcpServerCatalogStatus("playwright", "ok", "tools/list succeeded.", 1)]);

        await store.SaveAsync(new ToolVisibilityUpdateRequest
        {
            Mode = "custom",
            ToolAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["playwright.browser_navigate"] = ToolVisibilityStore.ConnectionA,
                ["github-mcp.create_issue"] = ToolVisibilityStore.ConnectionB
            }
        });

        Assert.True(store.IsToolEnabledForConnection("playwright.browser_navigate", ToolVisibilityStore.ConnectionA));
        Assert.False(store.IsToolEnabledForConnection("playwright.browser_navigate", ToolVisibilityStore.ConnectionB));
        Assert.True(store.IsToolEnabledForConnection("github-mcp.create_issue", ToolVisibilityStore.ConnectionB));
    }

    [Fact]
    public async Task ShardsExportAssignedExternalToolsOnly()
    {
        using var temp = new TempToolVisibilityConfig();
        var store = temp.CreateStore();
        var externalTools = new[]
        {
            Tool("playwright.browser_navigate"),
            Tool("context7.resolve-library-id")
        };
        store.RememberCatalog([Tool("window_list")], externalTools, []);
        await store.SaveAsync(new ToolVisibilityUpdateRequest
        {
            Mode = "custom",
            ToolAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["window_list"] = ToolVisibilityStore.ConnectionA,
                ["playwright.browser_navigate"] = ToolVisibilityStore.ConnectionA,
                ["context7.resolve-library-id"] = ToolVisibilityStore.ConnectionB
            }
        });

        var shardA = McpShardRuntime.ExportToolsForConnection([Tool("window_list")], externalTools, store, ToolVisibilityStore.ConnectionA);
        var shardB = McpShardRuntime.ExportToolsForConnection([Tool("window_list")], externalTools, store, ToolVisibilityStore.ConnectionB);

        Assert.Contains(shardA, tool => tool.Name == "window_list");
        Assert.Contains(shardA, tool => tool.Name == "playwright.browser_navigate");
        Assert.DoesNotContain(shardA, tool => tool.Name == "context7.resolve-library-id");
        Assert.Contains(shardB, tool => tool.Name == "context7.resolve-library-id");
        Assert.DoesNotContain(shardB, tool => tool.Name == "playwright.browser_navigate");
    }

    [Fact]
    public async Task ExternalToolCallRoutesToExternalMcpRouter()
    {
        using var temp = new TempToolVisibilityConfig();
        var store = temp.CreateStore();
        store.RememberCatalog([], [Tool("playwright.browser_navigate")], []);
        await store.SaveAsync(new ToolVisibilityUpdateRequest
        {
            Mode = "custom",
            ToolAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["playwright.browser_navigate"] = ToolVisibilityStore.ConnectionA
            }
        });

        var router = Substitute.For<IExternalMcpRouter>();
        router.IsExternalToolName("playwright.browser_navigate").Returns(true);
        router.CallToolAsync(Arg.Any<CallToolRequestParams>(), Arg.Any<CancellationToken>())
            .Returns(new CallToolResult { Content = [new TextContentBlock { Text = "external" }] });
        var localCalled = false;

        var result = await McpShardRuntime.CallToolAsync(
            new CallToolRequestParams { Name = "playwright.browser_navigate" },
            ToolVisibilityStore.ConnectionA,
            store,
            router,
            (_, _) =>
            {
                localCalled = true;
                return Task.FromResult(new CallToolResult());
            },
            CancellationToken.None);

        Assert.False(localCalled);
        Assert.Equal("external", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        await router.Received(1).CallToolAsync(
            Arg.Is<CallToolRequestParams>(request => request.Name == "playwright.browser_navigate"),
            Arg.Any<CancellationToken>());
    }

    private static Tool Tool(string name) => new()
    {
        Name = name,
        Title = name
    };

    private sealed class TempToolVisibilityConfig : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentBridgeTests", Guid.NewGuid().ToString("N"));

        public ToolVisibilityStore CreateStore()
        {
            Directory.CreateDirectory(_directory);
            return new ToolVisibilityStore(
                NullLogger<ToolVisibilityStore>.Instance,
                Path.Combine(_directory, "tool-visibility.json"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}
