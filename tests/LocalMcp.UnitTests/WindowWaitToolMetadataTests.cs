using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class WindowWaitToolMetadataTests
{
    [Fact]
    public void WindowWait_HasExpectedMetadataAndSchema()
    {
        var method = typeof(WindowWaitTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "window_wait");
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(attribute);
        Assert.True(attribute!.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
        Assert.Equal(
            new[]
            {
                "deviceId",
                "windowHandle",
                "processId",
                "processName",
                "className",
                "title",
                "titleContains",
                "occurrenceIndex",
                "condition",
                "expectedTitle",
                "includeInvisible",
                "timeoutMs",
                "pollIntervalMs"
            },
            method.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void WindowWait_DoesNotExposeInternalParameters()
    {
        var method = typeof(WindowWaitTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "window_wait");
        var forbidden = new[] { "CancellationToken", "HttpContext", "ClaimsPrincipal", "IServiceProvider", "Object" };

        foreach (var parameter in method.GetParameters())
            Assert.DoesNotContain(parameter.ParameterType.Name, forbidden);
    }
}
