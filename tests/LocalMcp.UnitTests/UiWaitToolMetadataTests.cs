using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class UiWaitToolMetadataTests
{
    [Fact]
    public void UiWait_HasExpectedMetadataAndSchema()
    {
        var method = typeof(UiWaitTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "ui_wait");
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
                "automationId",
                "name",
                "controlType",
                "occurrenceIndex",
                "condition",
                "expectedValue",
                "timeoutMs",
                "pollIntervalMs"
            },
            method.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void UiWait_DoesNotExposeInternalParameters()
    {
        var method = typeof(UiWaitTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "ui_wait");
        var forbidden = new[] { "CancellationToken", "HttpContext", "ClaimsPrincipal", "IServiceProvider", "Object" };

        foreach (var parameter in method.GetParameters())
            Assert.DoesNotContain(parameter.ParameterType.Name, forbidden);
    }
}
