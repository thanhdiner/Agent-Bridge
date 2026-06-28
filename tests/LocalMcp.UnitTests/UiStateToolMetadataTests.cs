using System.Reflection;
using System.Text.Json;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Results;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class UiStateToolMetadataTests
{
    [Fact]
    public void UiGetState_HasExpectedMetadataAndSchema()
    {
        var method = typeof(UiStateTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "ui_get_state");
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(attribute);
        Assert.True(attribute!.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
        Assert.Equal(
            new[]
            {
                "deviceId",
                "windowHandle",
                "automationId",
                "name",
                "controlType",
                "occurrenceIndex",
                "focusWindow"
            },
            method.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void UiGetState_NullPatternStates_AreSerializedExplicitly()
    {
        var result = new UiGetStateResult
        {
            WindowHandle = "0x1",
            Name = "Text editor",
            AutomationId = string.Empty,
            ControlType = "Document",
            Bounds = new UiBounds(),
            Enabled = true,
            Focused = false,
            Offscreen = false,
            KeyboardFocusable = true,
            OccurrenceIndex = 0
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, JsonOptions.Default));
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("selected").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("checked").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("checkState").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("expanded").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("expandCollapseState").ValueKind);
    }

    [Fact]
    public void UiGetState_DoesNotExposeInternalParameters()
    {
        var method = typeof(UiStateTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "ui_get_state");
        var forbidden = new[] { "CancellationToken", "HttpContext", "ClaimsPrincipal", "IServiceProvider", "Object" };

        foreach (var parameter in method.GetParameters())
            Assert.DoesNotContain(parameter.ParameterType.Name, forbidden);
    }
}
