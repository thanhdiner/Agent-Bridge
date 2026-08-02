using System.Text.Json;
using LocalMcp.Gateway.Mcp;

namespace LocalMcp.UnitTests;

public sealed class ToolSchemaSanitizerTests
{
    [Fact]
    public void RemoveRequiredProperty_RemovesDeviceIdAndKeepsOtherRequiredInputs()
    {
        using var doc = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "deviceId": { "type": "string" },
            "path": { "type": "string" }
          },
          "required": ["deviceId", "path"]
        }
        """);

        var schema = ToolSchemaSanitizer.RemoveRequiredProperty(doc.RootElement, "deviceId");

        Assert.True(schema.TryGetProperty("required", out var required));
        var requiredNames = required.EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert.Equal(new[] { "path" }, requiredNames);
    }

    [Fact]
    public void RemoveRequiredProperty_RemovesRequiredArrayWhenOnlyDeviceIdWasRequired()
    {
        using var doc = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "deviceId": { "type": "string" }
          },
          "required": ["deviceId"]
        }
        """);

        var schema = ToolSchemaSanitizer.RemoveRequiredProperty(doc.RootElement, "deviceId");

        Assert.False(schema.TryGetProperty("required", out _));
    }
}
