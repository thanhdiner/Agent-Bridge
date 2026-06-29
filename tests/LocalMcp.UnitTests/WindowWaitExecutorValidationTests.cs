using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalMcp.UnitTests;

public sealed class WindowWaitExecutorValidationTests
{
    private static UiAutomationExecutor CreateExecutor() =>
        new(NullLogger<UiAutomationExecutor>.Instance);

    [Theory]
    [InlineData("exists", "exists")]
    [InlineData(" NOT-EXISTS ", "not-exists")]
    [InlineData("Foreground", "foreground")]
    [InlineData("focused", "foreground")]
    [InlineData("appears", "exists")]
    [InlineData("disappears", "not-exists")]
    [InlineData("TITLE-EQUALS", "title-equals")]
    [InlineData("title-contains", "title-contains")]
    public void Conditions_NormalizeSupportedValues(string input, string expected)
    {
        Assert.True(WindowWaitConditions.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("visible")]
    public void Conditions_RejectUnsupportedValues(string? input) =>
        Assert.False(WindowWaitConditions.TryNormalize(input, out _));

    [Fact]
    public async Task MissingSelector_ReturnsInvalidRequest()
    {
        var result = await WaitAsync();
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task InvalidHandle_ReturnsInvalidRequest()
    {
        var result = await WaitAsync(windowHandle: "not-a-handle");
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidProcessId_ReturnsInvalidRequest(int processId)
    {
        var result = await WaitAsync(processId: processId);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData("visible", null)]
    [InlineData("title-equals", null)]
    [InlineData("title-contains", null)]
    public async Task InvalidConditionInputs_ReturnInvalidRequest(string condition, string? expectedTitle)
    {
        var result = await WaitAsync(
            processName: "notepad",
            condition: condition,
            expectedTitle: expectedTitle);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData(-1, 1000, 100)]
    [InlineData(1001, 1000, 100)]
    [InlineData(0, 0, 100)]
    [InlineData(0, 300001, 100)]
    [InlineData(0, 1000, 24)]
    [InlineData(0, 1000, 5001)]
    public async Task InvalidBounds_ReturnInvalidRequest(
        int occurrenceIndex,
        int timeoutMs,
        int pollIntervalMs)
    {
        var result = await WaitAsync(
            processName: "notepad",
            occurrenceIndex: occurrenceIndex,
            timeoutMs: timeoutMs,
            pollIntervalMs: pollIntervalMs);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task ValidSelector_ReachesPollingAndTimesOut()
    {
        var result = await WaitAsync(
            processName: "process-that-does-not-exist-7f2408d9",
            timeoutMs: 1,
            pollIntervalMs: 25);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WindowWaitTimeout, result.Error?.Code);
    }

    [Fact]
    public void CommandTimeout_AddsTransportBuffer()
    {
        var command = new WindowWaitCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessName = "notepad",
            TimeoutMs = 25_000
        };
        Assert.Equal(TimeSpan.FromSeconds(35), AgentCommandTimeouts.GetTimeout(command));
    }

    private static Task<CommandResult<WindowWaitResult>> WaitAsync(
        string? windowHandle = null,
        int? processId = null,
        string? processName = null,
        int occurrenceIndex = 0,
        string condition = WindowWaitConditions.Exists,
        string? expectedTitle = null,
        int timeoutMs = 1000,
        int pollIntervalMs = 100) =>
        CreateExecutor().WaitForWindowAsync(
            windowHandle,
            processId,
            processName,
            className: null,
            title: null,
            titleContains: null,
            occurrenceIndex,
            condition,
            expectedTitle,
            includeInvisible: false,
            timeoutMs,
            pollIntervalMs,
            Guid.NewGuid(),
            CancellationToken.None);
}
