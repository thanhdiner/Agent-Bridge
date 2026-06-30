using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class WorkspaceToolMetadataTests
{
    [Theory]
    [InlineData("workspace_list", new[] { "deviceId" })]
    [InlineData("workspace_resolve", new[] { "deviceId", "workspace", "relativePath", "requireWritable" })]
    public void WorkspaceTools_Have_Stable_Public_Schemas(
        string toolName,
        string[] expectedParameters)
    {
        var method = FindTool(toolName);
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(attribute);
        Assert.True(attribute!.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
        Assert.Equal(expectedParameters, method.GetParameters().Select(parameter => parameter.Name));
    }

    [Theory]
    [InlineData("workspace_list")]
    [InlineData("workspace_resolve")]
    public void WorkspaceTools_Do_Not_Expose_Framework_Parameters(string toolName)
    {
        var forbidden = new[]
        {
            "CancellationToken",
            "HttpContext",
            "ClaimsPrincipal",
            "IServiceProvider",
            "Object"
        };

        foreach (var parameter in FindTool(toolName).GetParameters())
            Assert.DoesNotContain(parameter.ParameterType.Name, forbidden);
    }

    private static MethodInfo FindTool(string toolName) =>
        typeof(WorkspaceTools)
            .GetMethods()
            .Single(method =>
                method.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
}
