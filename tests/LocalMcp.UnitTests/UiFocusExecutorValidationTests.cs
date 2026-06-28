using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalMcp.UnitTests;

public sealed class UiFocusExecutorValidationTests
{
    private static UiAutomationExecutor CreateExecutor() =>
        new(NullLogger<UiAutomationExecutor>.Instance);

    [Fact]
    public async Task FocusControlAsync_InvalidHandle_ReturnsInvalidRequest()
    {
        var result = await CreateExecutor().FocusControlAsync(
            "not-a-handle",
            automationId: null,
            name: "Search",
            controlType: "Edit",
            occurrenceIndex: 0,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task FocusControlAsync_MissingSelector_ReturnsInvalidRequest()
    {
        var result = await CreateExecutor().FocusControlAsync(
            "0x1234",
            automationId: null,
            name: null,
            controlType: null,
            occurrenceIndex: 0,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task FocusControlAsync_InvalidOccurrenceIndex_ReturnsInvalidRequest(int occurrenceIndex)
    {
        var result = await CreateExecutor().FocusControlAsync(
            "0x1234",
            automationId: "searchBox",
            name: null,
            controlType: "Edit",
            occurrenceIndex,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }
}
