using System.Reflection;
using LocalMcp.Gateway.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class ChromeTargetToolMetadataTests
{
    [Theory]
    [InlineData("chrome_targets_list", true, true, "urlContains", "maxResults")]
    [InlineData("chrome_target_snapshot", true, true, "targetId", "frameId", "maxNodes")]
    [InlineData("chrome_target_evaluate", false, false, "targetId", "function", "frameId")]
    public void Tools_ExposeExpectedSchema(string name, bool readOnly, bool idempotent, params string[] parameters)
    {
        var method = typeof(ChromeTargetTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == name);
        var metadata = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.Equal(readOnly, metadata.ReadOnly);
        Assert.False(metadata.Destructive);
        Assert.Equal(idempotent, metadata.Idempotent);
        Assert.True(metadata.OpenWorld);
        Assert.Equal(parameters, method.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void ToolVisibility_GroupsTargetsAndMarksEvaluationDangerous()
    {
        var path = Path.Combine(Path.GetTempPath(), "AgentBridgeTests", Guid.NewGuid().ToString("N"), "visibility.json");
        var store = new ToolVisibilityStore(NullLogger<ToolVisibilityStore>.Instance, path);
        store.RememberCatalog(
            [
                new Tool { Name = "chrome_targets_list" },
                new Tool { Name = "chrome_target_snapshot" },
                new Tool { Name = "chrome_target_evaluate" }
            ], []);

        var tools = store.GetSnapshot().Groups.SelectMany(group => group.Tools).ToArray();
        Assert.All(tools, tool => Assert.Equal("Browser", tool.Group));
        Assert.Equal("safe", Assert.Single(tools, tool => tool.Name == "chrome_targets_list").Risk);
        Assert.Equal("safe", Assert.Single(tools, tool => tool.Name == "chrome_target_snapshot").Risk);
        Assert.Equal("dangerous", Assert.Single(tools, tool => tool.Name == "chrome_target_evaluate").Risk);
    }
}
