using System.IO;
using System.Reflection;
using ModelContextProtocol.Server;
using LocalMcp.Gateway.Mcp;
using Xunit;

namespace LocalMcp.UnitTests;

public sealed class McpToolMetadataTests
{
    [Theory]
    [InlineData("fs_read", true, false)]
    [InlineData("fs_tree", true, false)]
    [InlineData("fs_list", true, false)]
    [InlineData("fs_search", true, false)]
    public void McpTools_ShouldHaveCorrectMetadata(string toolName, bool expectedReadOnly, bool expectedDestructive)
    {
        var methods = typeof(FileSystemTools).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var method = methods.FirstOrDefault(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);

        Assert.NotNull(method);
        var attr = method.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(expectedReadOnly, attr.ReadOnly);
        Assert.Equal(expectedDestructive, attr.Destructive);
    }

    [Fact]
    public void DumpMcpTypes()
    {
        var gwDir = Path.GetDirectoryName(typeof(FileSystemTools).Assembly.Location)!;
        var mcpAssembly = Assembly.Load("ModelContextProtocol");
        var mcpCoreAssembly = Assembly.Load("ModelContextProtocol.Core");
        var mcpAspNetAssembly = Assembly.Load("ModelContextProtocol.AspNetCore");

        var sb = new System.Text.StringBuilder();
        foreach (var ass in new[] { mcpAssembly, mcpCoreAssembly, mcpAspNetAssembly })
        {
            sb.AppendLine($"=== ASSEMBLY: {ass.GetName().Name} ===");
            foreach (var type in ass.GetTypes().Where(t => t.IsPublic))
            {
                sb.AppendLine($"Type: {type.FullName}");
                foreach (var prop in type.GetProperties())
                {
                    sb.AppendLine($"  Prop: {prop.PropertyType.Name} {prop.Name}");
                }
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    sb.AppendLine($"  Method: {method.ReturnType.Name} {method.Name}");
                }
            }
        }
        File.WriteAllText(Path.Combine(gwDir, "mcp_types_dump.txt"), sb.ToString());
    }
}
