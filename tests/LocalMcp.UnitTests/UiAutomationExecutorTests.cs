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

    [Fact]
    public void CreateFindOccurrenceKey_FollowsRequestedSelectorIdentity()
    {
        var firstCloseByName = UiAutomationExecutor.CreateFindOccurrenceKey(
            "Close",
            "view_4",
            "Button",
            preferAutomationId: false);
        var secondCloseByName = UiAutomationExecutor.CreateFindOccurrenceKey(
            "close",
            string.Empty,
            "button",
            preferAutomationId: false);
        var firstByAutomationId = UiAutomationExecutor.CreateFindOccurrenceKey(
            "Line 14, Column 1",
            "ContentTextBlock",
            "Text",
            preferAutomationId: true);
        var secondByAutomationId = UiAutomationExecutor.CreateFindOccurrenceKey(
            "645 characters",
            "contenttextblock",
            "text",
            preferAutomationId: true);
        var differentName = UiAutomationExecutor.CreateFindOccurrenceKey(
            "Minimize",
            "view_4",
            "Button",
            preferAutomationId: false);
        var differentControlType = UiAutomationExecutor.CreateFindOccurrenceKey(
            "Close",
            "view_4",
            "Pane",
            preferAutomationId: false);

        Assert.Equal(firstCloseByName, secondCloseByName, ignoreCase: true);
        Assert.Equal(firstByAutomationId, secondByAutomationId, ignoreCase: true);
        Assert.False(string.Equals(firstCloseByName, differentName, StringComparison.OrdinalIgnoreCase));
        Assert.False(string.Equals(firstCloseByName, differentControlType, StringComparison.OrdinalIgnoreCase));
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

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task ListWindowsAsync_InvalidLimit_ReturnsInvalidRequest(int maxWindows)
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.ListWindowsAsync(
            includeInvisible: false,
            includeUntitled: false,
            maxWindows,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task FindAsync_WithoutSelector_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.FindAsync(
            "0x1234",
            automationId: null,
            nameContains: null,
            controlType: null,
            maxDepth: 8,
            maxResults: 50,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(21, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task FindAsync_InvalidBounds_ReturnsInvalidRequest(int maxDepth, int maxResults)
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.FindAsync(
            "0x1234",
            automationId: null,
            nameContains: "Search",
            controlType: null,
            maxDepth,
            maxResults,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task FindAsync_OnInteractiveDesktop_FindsRootControl()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var listResult = await executor.ListWindowsAsync(
            includeInvisible: false,
            includeUntitled: false,
            maxWindows: 100,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(listResult.Success, listResult.Error?.Message);
        Assert.NotNull(listResult.Data);
        if (listResult.Data.Count == 0)
            return;

        var target = listResult.Data.Windows.FirstOrDefault(window => window.IsForeground)
            ?? listResult.Data.Windows[0];
        var treeResult = await executor.GetTreeAsync(
            target.WindowHandle,
            maxDepth: 0,
            maxNodes: 1,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(treeResult.Success, treeResult.Error?.Message);
        Assert.NotNull(treeResult.Data);

        var root = treeResult.Data.Root;
        var findResult = await executor.FindAsync(
            target.WindowHandle,
            automationId: null,
            nameContains: string.IsNullOrWhiteSpace(root.Name) ? null : root.Name,
            controlType: string.IsNullOrWhiteSpace(root.Name) ? root.ControlType : null,
            maxDepth: 0,
            maxResults: 10,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(findResult.Success, findResult.Error?.Message);
        Assert.NotNull(findResult.Data);
        var match = Assert.Single(findResult.Data.Matches);
        Assert.Equal(root.Name, match.Name);
        Assert.Equal(root.ControlType, match.ControlType);
        Assert.Equal(0, match.OccurrenceIndex);
        Assert.Equal(0, match.Depth);
    }

    [Fact]
    public async Task ListWindowsAsync_OnInteractiveDesktop_ChainsToUiTree()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var listResult = await executor.ListWindowsAsync(
            includeInvisible: false,
            includeUntitled: false,
            maxWindows: 100,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(listResult.Success, listResult.Error?.Message);
        Assert.NotNull(listResult.Data);
        if (listResult.Data.Count == 0)
            return;

        Assert.All(listResult.Data.Windows, window =>
        {
            Assert.True(window.IsVisible);
            Assert.False(window.IsCloaked);
            Assert.False(string.IsNullOrWhiteSpace(window.Title));
            Assert.StartsWith("0x", window.WindowHandle, StringComparison.OrdinalIgnoreCase);
            Assert.True(ulong.TryParse(window.WindowHandleDecimal, out _));
        });

        var target = listResult.Data.Windows.FirstOrDefault(window => window.IsForeground)
            ?? listResult.Data.Windows[0];
        var treeResult = await executor.GetTreeAsync(
            target.WindowHandle,
            maxDepth: 3,
            maxNodes: 500,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(treeResult.Success, treeResult.Error?.Message);
        Assert.NotNull(treeResult.Data);
        Assert.Equal(target.WindowHandle, treeResult.Data.WindowHandle, ignoreCase: true);
        Assert.Equal(target.ProcessId, treeResult.Data.ProcessId);

        Console.WriteLine($"WINDOW_LIST_SMOKE count={listResult.Data.Count} handle={target.WindowHandle} title={target.Title} process={target.ProcessName} nodes={treeResult.Data.NodeCount}");
    }
}
