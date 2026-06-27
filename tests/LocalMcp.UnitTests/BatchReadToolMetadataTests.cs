using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class BatchReadToolMetadataTests
{
    private static MethodInfo ToolMethod => typeof(BatchReadTools)
        .GetMethods()
        .Single(method =>
            method.GetCustomAttribute<McpServerToolAttribute>()?.Name == "fs_batch_read");

    [Fact]
    public void FsBatchRead_HasReadOnlyMetadata()
    {
        var attribute = ToolMethod.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.True(attribute.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
    }

    [Fact]
    public void FsBatchRead_HasExactSchema()
    {
        var names = ToolMethod.GetParameters()
            .Select(parameter => parameter.Name)
            .ToHashSet();

        Assert.Equal(4, names.Count);
        Assert.Contains("deviceId", names);
        Assert.Contains("paths", names);
        Assert.Contains("maxBytesPerFile", names);
        Assert.Contains("maxTotalBytes", names);
    }
}
