using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class AndroidToolMetadataTests
{
    [Fact]
    public void AndroidTools_ExposeOnlyNamespacedToolNames()
    {
        var names = typeof(AndroidTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "android_device_list", "android_get_state", "android_open_app", "android_press_key",
            "android_screenshot", "android_swipe", "android_tap", "android_type_text", "android_ui_tree"
        ], names);
        Assert.All(names, name => Assert.StartsWith("android_", name, StringComparison.Ordinal));
    }
}
