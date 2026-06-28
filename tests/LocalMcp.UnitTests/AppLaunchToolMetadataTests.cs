using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class AppLaunchToolMetadataTests
{
    [Fact]
    public void AppLaunch_HasExpectedMetadataAndSchema()
    {
        var method = typeof(AppLaunchTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "app_launch");
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
                "executable",
                "arguments",
                "workingDirectory",
                "waitForWindow",
                "windowTitleContains",
                "timeoutMs",
                "pollIntervalMs"
            },
            method.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void AppLaunch_DoesNotExposeInternalParameters()
    {
        var method = typeof(AppLaunchTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "app_launch");
        var forbidden = new[] { "CancellationToken", "HttpContext", "ClaimsPrincipal", "IServiceProvider", "Object" };

        foreach (var parameter in method.GetParameters())
            Assert.DoesNotContain(parameter.ParameterType.Name, forbidden);
    }
}
