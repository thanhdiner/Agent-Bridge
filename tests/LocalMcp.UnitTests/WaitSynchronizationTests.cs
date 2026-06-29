using System.Reflection;
using System.Text.Json;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class WaitSynchronizationTests
{
    [Theory]
    [InlineData(typeof(UiWaitTools), "ui_wait")]
    [InlineData(typeof(WindowWaitTools), "window_wait")]
    [InlineData(typeof(ProcessWaitTools), "process_wait")]
    public void WaitTools_AreReadOnlyAndClosedWorld(Type toolType, string expectedName)
    {
        var method = toolType.GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == expectedName);
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(attribute);
        Assert.True(attribute!.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
    }

    [Fact]
    public void ProcessWaitTool_ExposesBoundedPollingArguments()
    {
        var method = typeof(ProcessWaitTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "process_wait");

        Assert.Equal(
            new[]
            {
                "deviceId", "processId", "processName", "occurrenceIndex",
                "condition", "timeoutMs", "pollIntervalMs"
            },
            method.GetParameters().Select(parameter => parameter.Name).ToArray());
    }

    [Fact]
    public void DeserializeProcessWaitCommand_PreservesFields()
    {
        const string json = "{\"commandId\":\"44444444-4444-4444-4444-444444444444\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\",\"processId\":42,\"processName\":\"notepad.exe\",\"occurrenceIndex\":2,\"condition\":\"not-exists\",\"timeoutMs\":25000,\"pollIntervalMs\":125}";

        var command = JsonSerializer.Deserialize<ProcessWaitCommand>(json, JsonOptions.Default);

        Assert.NotNull(command);
        Assert.Equal(42, command!.ProcessId);
        Assert.Equal("notepad.exe", command.ProcessName);
        Assert.Equal(2, command.OccurrenceIndex);
        Assert.Equal(ProcessWaitConditions.NotExists, command.Condition);
        Assert.Equal(25_000, command.TimeoutMs);
        Assert.Equal(125, command.PollIntervalMs);
    }

    [Fact]
    public void ProcessWaitCommand_DefaultsAreBounded()
    {
        var command = new ProcessWaitCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessName = "notepad"
        };

        Assert.Equal(ProcessWaitConditions.Exists, command.Condition);
        Assert.Equal(10_000, command.TimeoutMs);
        Assert.Equal(200, command.PollIntervalMs);
        Assert.Equal(0, command.OccurrenceIndex);
    }
}
