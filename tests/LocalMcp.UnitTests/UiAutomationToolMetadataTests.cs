using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class UiAutomationToolMetadataTests
{
    private static MethodInfo ToolMethod => typeof(UiAutomationTools)
        .GetMethods()
        .Single(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name == "ui_tree");

    [Fact]
    public void UiTree_HasExpectedAnnotations()
    {
        var attribute = ToolMethod.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attribute);
        Assert.True(attribute!.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
    }

    [Fact]
    public void UiTree_HasExactSchema()
    {
        var actual = ToolMethod.GetParameters().Select(parameter => parameter.Name!).ToHashSet();
        var expected = new[] { "deviceId", "windowHandle", "maxDepth", "maxNodes" }.ToHashSet();
        Assert.True(expected.SetEquals(actual));
    }

    [Fact]
    public void UiTree_DoesNotExposeInternalParameters()
    {
        var forbidden = new[] { "CancellationToken", "HttpContext", "ClaimsPrincipal", "IServiceProvider", "Object" };
        foreach (var parameter in ToolMethod.GetParameters())
            Assert.DoesNotContain(parameter.ParameterType.Name, forbidden);
    }
}
