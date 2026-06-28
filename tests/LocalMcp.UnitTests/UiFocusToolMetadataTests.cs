using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class UiFocusToolMetadataTests
{
    [Fact]
    public void UiFocus_HasExpectedMetadataAndSchema()
    {
        var method = typeof(UiFocusTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "ui_focus");
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(attribute);
        Assert.False(attribute!.ReadOnly);
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
                "occurrenceIndex"
            },
            method.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void UiFocus_DoesNotExposeInternalParameters()
    {
        var method = typeof(UiFocusTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "ui_focus");
        var forbidden = new[] { "CancellationToken", "HttpContext", "ClaimsPrincipal", "IServiceProvider", "Object" };

        foreach (var parameter in method.GetParameters())
            Assert.DoesNotContain(parameter.ParameterType.Name, forbidden);
    }
}
