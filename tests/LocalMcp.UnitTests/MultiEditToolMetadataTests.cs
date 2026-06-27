using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class MultiFilePatchToolMetadataTests
{
    private static MethodInfo ToolMethod => typeof(FileSystemTools)
        .GetMethods()
        .Single(method =>
            method.GetCustomAttribute<McpServerToolAttribute>()?.Name == "fs_batch_patch");

    [Fact]
    public void HasWriteMetadata()
    {
        var attribute = ToolMethod.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.False(attribute.ReadOnly);
        Assert.True(attribute.Destructive);
        Assert.False(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
    }

    [Fact]
    public void HasExactSchema()
    {
        var names = ToolMethod.GetParameters().Select(parameter => parameter.Name).ToHashSet();
        Assert.Equal(2, names.Count);
        Assert.Contains("deviceId", names);
        Assert.Contains("items", names);
    }
}
