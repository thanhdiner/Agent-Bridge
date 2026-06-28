using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalMcp.UnitTests;

public sealed class UiValueExecutorValidationTests
{
    private static UiAutomationExecutor CreateExecutor() =>
        new(NullLogger<UiAutomationExecutor>.Instance);

    [Fact]
    public async Task GetValueAsync_InvalidHandle_ReturnsInvalidRequest()
    {
        var result = await CreateExecutor().GetValueAsync(
            "not-a-handle",
            automationId: null,
            name: "Search",
            controlType: "Edit",
            occurrenceIndex: 0,
            focusWindow: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task GetValueAsync_MissingSelector_ReturnsInvalidRequest()
    {
        var result = await CreateExecutor().GetValueAsync(
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
    public async Task GetValueAsync_InvalidOccurrenceIndex_ReturnsInvalidRequest(int occurrenceIndex)
    {
        var result = await CreateExecutor().GetValueAsync(
            "0x1234",
            automationId: "searchBox",
            name: null,
            controlType: null,
            occurrenceIndex,
            focusWindow: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task SetValueAsync_AllowsEmptyValueForClear()
    {
        var result = await CreateExecutor().SetValueAsync(
            "not-a-handle",
            string.Empty,
            automationId: "searchBox",
            name: null,
            controlType: "Edit",
            occurrenceIndex: 0,
            focusWindow: true,
            append: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
        Assert.DoesNotContain("value is required", result.Error?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetValueAsync_ValueAboveLimit_ReturnsInvalidRequest()
    {
        var result = await CreateExecutor().SetValueAsync(
            "0x1234",
            new string('x', 65_537),
            automationId: "searchBox",
            name: null,
            controlType: "Edit",
            occurrenceIndex: 0,
            focusWindow: true,
            append: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task SetValueAsync_MissingSelector_ReturnsInvalidRequest()
    {
        var result = await CreateExecutor().SetValueAsync(
            "0x1234",
            "hello",
            automationId: null,
            name: null,
            controlType: null,
            occurrenceIndex: 0,
            focusWindow: true,
            append: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }
}
