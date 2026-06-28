using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class WindowActionToolMetadataTests
{
    [Theory]
    [InlineData(typeof(WindowActionTools), "window_focus", false, true, "deviceId", "windowHandle")]
    [InlineData(typeof(WindowActionTools), "window_move", false, true, "deviceId", "windowHandle", "x", "y", "width", "height", "restoreIfNeeded")]
    [InlineData(typeof(WindowCloseTools), "window_close", true, false, "deviceId", "windowHandle")]
    [InlineData(typeof(UiClickTools), "ui_click", true, false, "deviceId", "windowHandle", "automationId", "name", "controlType", "occurrenceIndex", "focusWindow")]
    public void Tool_HasExpectedMetadataAndSchema(
        Type toolType,
        string toolName,
        bool destructive,
        bool idempotent,
        params string[] expectedParameters)
    {
        var method = toolType.GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(attribute);
        Assert.False(attribute!.ReadOnly);
        Assert.Equal(destructive, attribute.Destructive);
        Assert.Equal(idempotent, attribute.Idempotent);
        Assert.False(attribute.OpenWorld);

        var actualParameters = method.GetParameters().Select(parameter => parameter.Name!).ToHashSet();
        Assert.True(expectedParameters.ToHashSet().SetEquals(actualParameters));
    }

    [Theory]
    [InlineData(typeof(WindowActionTools), "window_focus")]
    [InlineData(typeof(WindowActionTools), "window_move")]
    [InlineData(typeof(WindowCloseTools), "window_close")]
    [InlineData(typeof(UiClickTools), "ui_click")]
    public void Tool_DoesNotExposeInternalParameters(Type toolType, string toolName)
    {
        var method = toolType.GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        var forbidden = new[] { "CancellationToken", "HttpContext", "ClaimsPrincipal", "IServiceProvider", "Object" };

        foreach (var parameter in method.GetParameters())
            Assert.DoesNotContain(parameter.ParameterType.Name, forbidden);
    }
}
