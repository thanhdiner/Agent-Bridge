using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalMcp.UnitTests;

public sealed class UiStateExecutorValidationTests
{
    private static UiAutomationExecutor CreateExecutor() =>
        new(NullLogger<UiAutomationExecutor>.Instance);

    [Fact]
    public async Task GetStateAsync_InvalidHandle_ReturnsInvalidRequest()
    {
        var result = await CreateExecutor().GetStateAsync(
            "not-a-handle",
            automationId: null,
            name: "Remember me",
            controlType: "CheckBox",
            occurrenceIndex: 0,
            focusWindow: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task GetStateAsync_MissingSelector_ReturnsInvalidRequest()
    {
        var result = await CreateExecutor().GetStateAsync(
            "0x1234",
            automationId: null,
            name: null,
            controlType: null,
            occurrenceIndex: 0,
            focusWindow: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task GetStateAsync_InvalidOccurrenceIndex_ReturnsInvalidRequest(int occurrenceIndex)
    {
        var result = await CreateExecutor().GetStateAsync(
            "0x1234",
            automationId: "rememberMe",
            name: null,
            controlType: "CheckBox",
            occurrenceIndex,
            focusWindow: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }
}
