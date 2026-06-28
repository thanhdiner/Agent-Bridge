using Interop.UIAutomationClient;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalMcp.UnitTests;

public sealed class UiAutomationExecutorTests
{
    [Theory]
    [InlineData("0x1234", 0x1234)]
    [InlineData("4660", 4660)]
    [InlineData(" 0X1234 ", 0x1234)]
    public void TryParseWindowHandle_ValidValues_Succeeds(string value, long expected)
    {
        var success = UiAutomationExecutor.TryParseWindowHandle(value, out var handle);

        Assert.True(success);
        Assert.Equal(expected, handle.ToInt64());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0x")]
    [InlineData("0xGG")]
    public void TryParseWindowHandle_InvalidValues_Fails(string? value)
    {
        Assert.False(UiAutomationExecutor.TryParseWindowHandle(value, out var handle));
        Assert.Equal(IntPtr.Zero, handle);
    }

    [Fact]
    public void GetControlTypeName_KnownAndUnknownValues_AreStable()
    {
        Assert.Equal("Button", UiAutomationExecutor.GetControlTypeName(UIA_ControlTypeIds.UIA_ButtonControlTypeId));
        Assert.Equal("Document", UiAutomationExecutor.GetControlTypeName(UIA_ControlTypeIds.UIA_DocumentControlTypeId));
        Assert.Equal("Unknown", UiAutomationExecutor.GetControlTypeName(0));
        Assert.Equal("Unknown(59999)", UiAutomationExecutor.GetControlTypeName(59999));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(21, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1001)]
    public async Task GetTreeAsync_InvalidBounds_ReturnsInvalidRequest(int maxDepth, int maxNodes)
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.GetTreeAsync(
            "0x1234",
            maxDepth,
            maxNodes,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task GetTreeAsync_InvalidHandleFormat_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.GetTreeAsync(
            "not-a-handle",
            maxDepth: 6,
            maxNodes: 500,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }
}
