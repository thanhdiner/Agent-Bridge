using System.Text.Json;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalMcp.UnitTests;

public sealed class UiScrollTests
{
    [Fact]
    public void Deserialize_WithAllFields_Succeeds()
    {
        const string json = "{\"windowHandle\":\"0x1234\",\"direction\":\"down\",\"amount\":\"page\",\"automationId\":\"content\",\"name\":\"Document\",\"controlType\":\"Document\",\"occurrenceIndex\":2,\"focusWindow\":false,\"commandId\":\"00000000-0000-0000-0000-000000000001\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\"}";

        var command = JsonSerializer.Deserialize<UiScrollCommand>(json, JsonOptions.Default);

        Assert.NotNull(command);
        Assert.Equal("down", command.Direction);
        Assert.Equal("page", command.Amount);
        Assert.Equal("content", command.AutomationId);
        Assert.Equal("Document", command.Name);
        Assert.Equal("Document", command.ControlType);
        Assert.Equal(2, command.OccurrenceIndex);
        Assert.False(command.FocusWindow);
    }

    [Fact]
    public void Deserialize_WithoutOptionalFields_UsesDefaults()
    {
        const string json = "{\"windowHandle\":\"0x1234\",\"direction\":\"up\",\"commandId\":\"00000000-0000-0000-0000-000000000001\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-29T00:00:00Z\"}";

        var command = JsonSerializer.Deserialize<UiScrollCommand>(json, JsonOptions.Default);

        Assert.NotNull(command);
        Assert.Equal(UiScrollAmounts.Page, command.Amount);
        Assert.Equal(0, command.OccurrenceIndex);
        Assert.True(command.FocusWindow);
    }

    [Theory]
    [InlineData("UP", "up")]
    [InlineData(" down ", "down")]
    [InlineData("Left", "left")]
    [InlineData("right", "right")]
    public void Direction_Normalizes(string input, string expected)
    {
        Assert.True(UiScrollDirections.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("small", "small")]
    [InlineData(" PAGE ", "page")]
    [InlineData("End", "end")]
    public void Amount_Normalizes(string input, string expected)
    {
        Assert.True(UiScrollAmounts.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("diagonal", "page")]
    [InlineData("down", "huge")]
    public async Task InvalidDirectionOrAmount_ReturnsInvalidRequest(string direction, string amount)
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.ScrollAsync(
            "0x1234",
            direction,
            amount,
            null,
            "Document",
            "Document",
            0,
            true,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData("up", "small", "UP")]
    [InlineData("down", "small", "DOWN")]
    [InlineData("up", "page", "PAGEUP")]
    [InlineData("down", "page", "PAGEDOWN")]
    [InlineData("up", "end", "HOME")]
    [InlineData("down", "end", "END")]
    public void KeyboardFallback_MapsVerticalScroll(string direction, string amount, string expected)
    {
        Assert.Equal(expected, UiAutomationExecutor.GetKeyboardScrollKeys(direction, amount));
    }

    [Fact]
    public void KeyboardFallback_RejectsHorizontalScroll()
    {
        Assert.Null(UiAutomationExecutor.GetKeyboardScrollKeys(UiScrollDirections.Left, UiScrollAmounts.Page));
    }

    [Fact]
    public void ScrollItemFallback_ScoresOnlyOffscreenItemsInCentralContentBand()
    {
        var viewport = new LocalMcp.Contracts.Results.UiBounds
        {
            X = 240,
            Y = 50,
            Width = 1_680,
            Height = 980
        };

        var pageAbove = new LocalMcp.Contracts.Results.UiBounds
        {
            X = 900,
            Y = -700,
            Width = 300,
            Height = 20
        };
        var sidebarAbove = new LocalMcp.Contracts.Results.UiBounds
        {
            X = 260,
            Y = -700,
            Width = 100,
            Height = 20
        };
        var visible = new LocalMcp.Contracts.Results.UiBounds
        {
            X = 900,
            Y = 200,
            Width = 300,
            Height = 20
        };
        var rightAlignedBelow = new LocalMcp.Contracts.Results.UiBounds
        {
            X = 1_500,
            Y = 1_200,
            Width = 100,
            Height = 20
        };

        Assert.NotNull(UiAutomationExecutor.GetScrollItemCandidateScore(
            pageAbove,
            viewport,
            UiScrollDirections.Up,
            UiScrollAmounts.Page));
        Assert.Null(UiAutomationExecutor.GetScrollItemCandidateScore(
            sidebarAbove,
            viewport,
            UiScrollDirections.Up,
            UiScrollAmounts.Page));
        Assert.Null(UiAutomationExecutor.GetScrollItemCandidateScore(
            visible,
            viewport,
            UiScrollDirections.Up,
            UiScrollAmounts.Page));
        Assert.NotNull(UiAutomationExecutor.GetScrollItemCandidateScore(
            rightAlignedBelow,
            viewport,
            UiScrollDirections.Down,
            UiScrollAmounts.Page));
    }

    [Fact]
    public void ScrollItemFallback_RejectsLargeContainerOverlappingViewport()
    {
        var viewport = new LocalMcp.Contracts.Results.UiBounds
        {
            X = 240,
            Y = 50,
            Width = 1_680,
            Height = 980
        };
        var overlappingContainer = new LocalMcp.Contracts.Results.UiBounds
        {
            X = 240,
            Y = -2_000,
            Width = 1_680,
            Height = 3_000
        };
        var fullyAbove = new LocalMcp.Contracts.Results.UiBounds
        {
            X = 800,
            Y = -2_000,
            Width = 400,
            Height = 100
        };

        Assert.Null(UiAutomationExecutor.GetScrollItemCandidateScore(
            overlappingContainer,
            viewport,
            UiScrollDirections.Up,
            UiScrollAmounts.Page));
        Assert.NotNull(UiAutomationExecutor.GetScrollItemCandidateScore(
            fullyAbove,
            viewport,
            UiScrollDirections.Up,
            UiScrollAmounts.Page));
    }

    [Fact]
    public void ScrollItemFallback_EndPrefersFarthestItem()
    {
        var viewport = new LocalMcp.Contracts.Results.UiBounds
        {
            X = 0,
            Y = 0,
            Width = 1_000,
            Height = 800
        };
        var nearAbove = new LocalMcp.Contracts.Results.UiBounds
        {
            X = 400,
            Y = -100,
            Width = 100,
            Height = 20
        };
        var farAbove = new LocalMcp.Contracts.Results.UiBounds
        {
            X = 400,
            Y = -2_000,
            Width = 100,
            Height = 20
        };

        var nearScore = UiAutomationExecutor.GetScrollItemCandidateScore(
            nearAbove,
            viewport,
            UiScrollDirections.Up,
            UiScrollAmounts.End);
        var farScore = UiAutomationExecutor.GetScrollItemCandidateScore(
            farAbove,
            viewport,
            UiScrollDirections.Up,
            UiScrollAmounts.End);

        Assert.NotNull(nearScore);
        Assert.NotNull(farScore);
        Assert.True(farScore < nearScore);
    }

    [Fact]
    public void ViewportVerification_AcceptsStrongDirectionalMovementFromOneFreshMarker()
    {
        var before = new[]
        {
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fmarker", 100, 20)
        };
        var after = new[]
        {
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fmarker", 500, 20)
        };

        Assert.True(UiAutomationExecutor.HasViewportSnapshotMoved(
            before,
            after,
            UiScrollDirections.Up));
        Assert.False(UiAutomationExecutor.HasViewportSnapshotMoved(
            before,
            after,
            UiScrollDirections.Down));
    }

    [Fact]
    public void ViewportVerification_AcceptsQuorumOfSmallDirectionalMovements()
    {
        var before = new[]
        {
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fa", 100, 20),
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fb", 200, 20)
        };
        var after = new[]
        {
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fa", 104, 20),
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fb", 205, 20)
        };

        Assert.True(UiAutomationExecutor.HasViewportSnapshotMoved(
            before,
            after,
            UiScrollDirections.Up));
    }

    [Fact]
    public void ViewportVerification_AcceptsRecycledVisibleMarkerSet()
    {
        var before = new[]
        {
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fa", 100, 20),
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fb", 200, 20),
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fc", 300, 20),
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fd", 400, 20)
        };
        var after = new[]
        {
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fx", 100, 20),
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fy", 200, 20),
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fz", 300, 20),
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fw", 400, 20)
        };

        Assert.True(UiAutomationExecutor.HasViewportSnapshotMoved(
            before,
            after,
            UiScrollDirections.Down));
    }

    [Fact]
    public void ViewportVerification_RejectsStableSnapshot()
    {
        var before = new[]
        {
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fa", 100, 20),
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fb", 200, 20),
            new UiAutomationExecutor.ScrollVerificationMarker("Text\u001f\u001fc", 300, 20)
        };

        Assert.False(UiAutomationExecutor.HasViewportSnapshotMoved(
            before,
            before,
            UiScrollDirections.Down));
    }

    [Fact]
    public async Task ControlTypeOnlySelector_IsAccepted()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.ScrollAsync(
            "0x1",
            UiScrollDirections.Down,
            UiScrollAmounts.Page,
            null,
            null,
            "Pane",
            0,
            true,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotEqual(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task MissingSelector_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.ScrollAsync(
            "0x1234",
            UiScrollDirections.Down,
            UiScrollAmounts.Page,
            null,
            null,
            null,
            0,
            true,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }
}
