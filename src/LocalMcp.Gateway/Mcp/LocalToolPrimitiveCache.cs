using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;

namespace LocalMcp.Gateway.Mcp;

public sealed class LocalToolPrimitiveCache
{
    private readonly ConcurrentDictionary<string, object> _primitives = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Tool> _protocolTools = new(StringComparer.OrdinalIgnoreCase);

    public void Remember(IEnumerable<object> primitives)
    {
        foreach (var primitive in primitives)
        {
            var protocolTool = GetProtocolTool(primitive);
            var name = protocolTool?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            _primitives[name] = primitive;
            _protocolTools[name] = protocolTool!;
        }
    }

    public IReadOnlyList<Tool> ListProtocolTools() =>
        _protocolTools.Values
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public bool TryGetPrimitive(string toolName, out object? primitive)
    {
        if (_primitives.TryGetValue(toolName.Trim(), out var found))
        {
            primitive = found;
            return true;
        }

        primitive = null;
        return false;
    }

    private static Tool? GetProtocolTool(object primitive)
    {
        return primitive.GetType().GetProperty("ProtocolTool")?.GetValue(primitive) as Tool;
    }
}
