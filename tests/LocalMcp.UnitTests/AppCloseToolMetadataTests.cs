using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class AppCloseToolMetadataTests
{
    [Fact]
    public void AppClose_HasExpectedMetadataAndSchema()
    {
        var method = typeof(AppCloseTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "app_close");
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(attribute);
        Assert.False(attribute!.ReadOnly);
        Assert.True(attribute.Destructive);
        Assert.False(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
        Assert.Equal(
            new[]
            {
                "deviceId",
                "processId",
                "processName",
                "allMatches",
                "force",
                "entireProcessTree",
                "timeoutMs"
            },
            method.GetParameters().Select(parameter => parameter.Name));
    }
}
