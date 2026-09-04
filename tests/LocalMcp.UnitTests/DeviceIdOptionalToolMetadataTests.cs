using System.Reflection;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class DeviceIdOptionalToolMetadataTests
{
    [Fact]
    public void AllMcpToolDeviceIdParameters_AreOptional()
    {
        var failures = typeof(DeviceTools).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "LocalMcp.Gateway.Mcp")
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(method => method.GetParameters()
                .Where(parameter => parameter.Name == "deviceId")
                .Select(parameter => new
                {
                    ToolName = method.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? method.Name,
                    Parameter = parameter
                }))
            .Where(item => !item.Parameter.IsOptional || !item.Parameter.HasDefaultValue)
            .Select(item => item.ToolName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(failures);
    }
}
