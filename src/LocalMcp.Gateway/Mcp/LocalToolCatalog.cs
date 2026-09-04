using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalMcp.Gateway.Mcp;

public static class LocalToolCatalog
{
    public static IReadOnlyList<Tool> DiscoverFromAssembly(Assembly assembly) =>
        assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute?.Name))
            .Select(attribute => new Tool
            {
                Name = attribute!.Name!,
                Title = attribute.Name
            })
            .GroupBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
