using System.Reflection;
using System.Text.Json;
using Interop.UIAutomationClient;
using LocalMcp.Agent.Windows.Connection;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalMcp.UnitTests;

public sealed class UiTextReadTests
{
    [Theory]
    [InlineData("DOCUMENT", UiTextReadScopes.Document)]
    [InlineData(" visible ", UiTextReadScopes.Visible)]
    [InlineData("Selection", UiTextReadScopes.Selection)]
    public void Scope_Normalizes(string input, string expected)
    {
        Assert.True(UiTextReadScopes.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task InvalidScope_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.TextReadAsync(
            "0x1234", "screen", null, null, "Document", 0,
            0, 10, 1000, false,
            Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task MissingSelector_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.TextReadAsync(
            "0x1234", UiTextReadScopes.Document, null, null, null, 0,
            0, 10, 1000, false,
            Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task CharacterLimitAboveMaximum_ReturnsLimitError()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.TextReadAsync(
            "0x1234", UiTextReadScopes.Document, null, null, "Document", 0,
            0, 10, 65_537, false,
            Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.UiTextLimitExceeded, result.Error?.Code);
    }

    [Fact]
    public void UiFindPatternMetadata_ContainsTextPatterns()
    {
        var field = typeof(UiAutomationExecutor).GetField(
            "UiFindPatterns",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var patterns = Assert.IsType<(int PatternId, string Name)[]>(field.GetValue(null));

        Assert.Contains(patterns, item => item.PatternId == UIA_PatternIds.UIA_TextPatternId && item.Name == "text");
        Assert.Contains(patterns, item => item.PatternId == UIA_PatternIds.UIA_TextPattern2Id && item.Name == "text2");
    }

    [Theory]
    [InlineData("first\nsecond\n", 2)]
    [InlineData("first\rsecond\r", 2)]
    [InlineData("first\r\nsecond\r\n", 2)]
    [InlineData("first\r\nsecond", 2)]
    [InlineData("single", 1)]
    [InlineData("", 0)]
    public void CountTextLines_SupportsWindowsAndUnixEndings(string text, int expected)
    {
        Assert.Equal(expected, UiAutomationExecutor.CountTextLines(text));
    }

    [Fact]
    public void AgentDeserializer_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"UiTextReadCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"windowHandle\":\"0x1234\",\"scope\":\"visible\",\"automationId\":\"editor\",\"name\":\"Editor\",\"controlType\":\"Document\",\"occurrenceIndex\":2,\"startLine\":5,\"lineCount\":20,\"maxCharacters\":4096,\"focusWindow\":true}}";

        var command = DeserializeExtended(nameof(UiTextReadCommand), json);
        var textCommand = Assert.IsType<UiTextReadCommand>(command);
        Assert.Equal(UiTextReadScopes.Visible, textCommand.Scope);
        Assert.Equal("editor", textCommand.AutomationId);
        Assert.Equal("Editor", textCommand.Name);
        Assert.Equal("Document", textCommand.ControlType);
        Assert.Equal(2, textCommand.OccurrenceIndex);
        Assert.Equal(5, textCommand.StartLine);
        Assert.Equal(20, textCommand.LineCount);
        Assert.Equal(4096, textCommand.MaxCharacters);
        Assert.True(textCommand.FocusWindow);
    }

    [Fact]
    public void Command_RoundTripsWithDefaults()
    {
        var command = new UiTextReadCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = "0x1234",
            ControlType = "Document"
        };

        var json = JsonSerializer.Serialize(command, JsonOptions.Default);
        var roundTrip = JsonSerializer.Deserialize<UiTextReadCommand>(json, JsonOptions.Default);

        Assert.NotNull(roundTrip);
        Assert.Equal(UiTextReadScopes.Document, roundTrip.Scope);
        Assert.Equal(0, roundTrip.StartLine);
        Assert.Equal(200, roundTrip.LineCount);
        Assert.Equal(65_536, roundTrip.MaxCharacters);
        Assert.False(roundTrip.FocusWindow);
    }

    private static AgentCommand? DeserializeExtended(string commandType, string json)
    {
        var method = typeof(GatewayConnection).GetMethod(
            "DeserializeExtendedCommand",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<AgentCommand?>(method.Invoke(null, [commandType, json]));
    }
}
