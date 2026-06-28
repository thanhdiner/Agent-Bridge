using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class UiValueToolMetadataTests
{
    [Fact]
    public void UiGetValue_HasExpectedMetadataAndSchema()
    {
        var method = FindTool("ui_get_value");
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
                "focusWindow"
            },
            method.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void UiSetValue_HasExpectedMetadataAndSchema()
    {
        var method = FindTool("ui_set_value");
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
                "windowHandle",
                "value",
                "automationId",
                "name",
                "controlType",
                "occurrenceIndex",
                "focusWindow",
                "append"
            },
            method.GetParameters().Select(parameter => parameter.Name));
    }

    [Theory]
    [InlineData("ui_get_value")]
    [InlineData("ui_set_value")]
    public void UiValueTools_DoNotExposeInternalParameters(string toolName)
    {
        var method = FindTool(toolName);
        var forbidden = new[] { "CancellationToken", "HttpContext", "ClaimsPrincipal", "IServiceProvider", "Object" };

        foreach (var parameter in method.GetParameters())
            Assert.DoesNotContain(parameter.ParameterType.Name, forbidden);
    }

    private static MethodInfo FindTool(string toolName) =>
        typeof(UiValueTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
}
