using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalMcp.UnitTests;

public sealed class UiWaitExecutorValidationTests
{
    private static UiAutomationExecutor CreateExecutor() =>
        new(NullLogger<UiAutomationExecutor>.Instance);

    [Theory]
    [InlineData("exists", "exists")]
    [InlineData(" NOT-EXISTS ", "not-exists")]
    [InlineData("Enabled", "enabled")]
    [InlineData("disabled", "disabled")]
    [InlineData("focused", "focused")]
    [InlineData("disappears", "not-exists")]
    [InlineData("VALUE-EQUALS", "value-equals")]
    [InlineData("value-contains", "value-contains")]
    [InlineData("value_changed", "value-changed")]
    public void WaitCondition_Normalization_AcceptsSupportedValues(string input, string expected)
    {
        Assert.True(UiWaitConditions.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("visible")]
    public void WaitCondition_Normalization_RejectsUnsupportedValues(string? input)
    {
        Assert.False(UiWaitConditions.TryNormalize(input, out _));
    }

    [Fact]
    public async Task WaitAsync_InvalidHandle_ReturnsInvalidRequest()
    {
        var result = await CreateExecutor().WaitAsync(
            "not-a-handle",
            automationId: null,
            name: "Status",
            controlType: "Text",
            occurrenceIndex: 0,
            condition: UiWaitConditions.Exists,
            expectedValue: null,
            timeoutMs: 1000,
            pollIntervalMs: 100,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task WaitAsync_MissingSelector_ReturnsInvalidRequest()
    {
        var result = await CreateExecutor().WaitAsync(
            "0x1234",
            automationId: null,
            name: null,
            controlType: null,
            occurrenceIndex: 0,
            condition: UiWaitConditions.Exists,
            expectedValue: null,
            timeoutMs: 1000,
            pollIntervalMs: 100,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task WaitAsync_UnsupportedCondition_ReturnsInvalidRequest()
    {
        var result = await CreateExecutor().WaitAsync(
            "0x1234",
            automationId: "status",
            name: null,
            controlType: null,
            occurrenceIndex: 0,
            condition: "visible",
            expectedValue: null,
            timeoutMs: 1000,
            pollIntervalMs: 100,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData("value-equals")]
    [InlineData("value-contains")]
    public async Task WaitAsync_ValueConditionWithoutExpectedValue_ReturnsInvalidRequest(string condition)
    {
        var result = await CreateExecutor().WaitAsync(
            "0x1234",
            automationId: "status",
            name: null,
            controlType: null,
            occurrenceIndex: 0,
            condition,
            expectedValue: null,
            timeoutMs: 1000,
            pollIntervalMs: 100,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(300001, 100)]
    [InlineData(1000, 24)]
    [InlineData(1000, 5001)]
    public async Task WaitAsync_InvalidTiming_ReturnsInvalidRequest(int timeoutMs, int pollIntervalMs)
    {
        var result = await CreateExecutor().WaitAsync(
            "0x1234",
            automationId: "status",
            name: null,
            controlType: null,
            occurrenceIndex: 0,
            condition: UiWaitConditions.Exists,
            expectedValue: null,
            timeoutMs,
            pollIntervalMs,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public void AgentCommandTimeouts_UiWaitAddsTransportBuffer()
    {
        var command = new UiWaitCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = "0x1234",
            Name = "Status",
            TimeoutMs = 25_000
        };

        Assert.Equal(TimeSpan.FromSeconds(35), AgentCommandTimeouts.GetTimeout(command));
    }
}
