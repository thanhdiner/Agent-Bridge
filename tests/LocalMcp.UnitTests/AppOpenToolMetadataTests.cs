using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class AppOpenToolMetadataTests
{
    [Fact]
    public void AppOpen_HasExpectedMetadataAndSchema()
    {
        var method = typeof(AppOpenTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "app_open");
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(attribute);
        Assert.False(attribute!.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.False(attribute.Idempotent);
        Assert.True(attribute.OpenWorld);
        Assert.Equal(
            new[]
            {
                "deviceId",
                "appId",
                "arguments",
                "refresh",
                "focusIfRunning",
                "waitForWindow",
                "windowTitleContains",
                "timeoutMs",
                "pollIntervalMs"
            },
            method.GetParameters().Select(parameter => parameter.Name));
    }
}
