using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace LocalMcp.Gateway.Mcp;

public static class ToolSchemaSanitizer
{
    public static Tool NormalizeLocalToolSchema(Tool tool)
    {
        if (!ToolHasDeviceIdProperty(tool))
            return tool;

        tool.InputSchema = RemoveRequiredProperty(tool.InputSchema, "deviceId");
        return tool;
    }

    public static JsonElement RemoveRequiredProperty(JsonElement schema, string propertyName)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return schema.Clone();
        }

        var root = JsonNode.Parse(schema.GetRawText()) as JsonObject;
        var requiredNode = root?["required"] as JsonArray;
        if (root is null || requiredNode is null)
            return schema.Clone();

        var remaining = requiredNode
            .Select(node => node?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => !string.Equals(value, propertyName, StringComparison.Ordinal))
            .Select(value => JsonValue.Create(value)!)
            .ToArray();

        if (remaining.Length == requiredNode.Count)
            return schema.Clone();

        if (remaining.Length == 0)
        {
            root.Remove("required");
        }
        else
        {
            var newRequired = new JsonArray();
            foreach (var value in remaining)
                newRequired.Add(value);
            root["required"] = newRequired;
        }

        return JsonSerializer.SerializeToElement(root);
    }

    private static bool ToolHasDeviceIdProperty(Tool tool)
    {
        var schema = tool.InputSchema;
        return schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object &&
            properties.TryGetProperty("deviceId", out _);
    }
}
