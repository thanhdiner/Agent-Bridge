using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Gateway.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class WindowScreenshotTests
{
    [Fact]
    public void Tool_HasExpectedMetadataAndSchema()
    {
        var method = typeof(WindowActionTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "window_screenshot");
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(attribute);
        Assert.True(attribute!.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);

        var parameters = method.GetParameters().Select(parameter => parameter.Name!).ToHashSet();
        Assert.True(new[] { "deviceId", "windowHandle", "maxWidth", "maxHeight" }.ToHashSet().SetEquals(parameters));
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(4097, 1080)]
    [InlineData(1920, 0)]
    [InlineData(1920, 4097)]
    public async Task CaptureWindowScreenshotAsync_InvalidDimensions_ReturnsInvalidRequest(int maxWidth, int maxHeight)
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.CaptureWindowScreenshotAsync(
            "0x1234",
            maxWidth,
            maxHeight,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public void BgraPngEncoder_EncodesValidPixelData()
    {
        byte[] bgra = [3, 2, 1, 255];

        var png = BgraPngEncoder.Encode(bgra, 1, 1);

        Assert.True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)));

        var offset = 8;
        byte[]? compressed = null;
        while (offset < png.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT")
                compressed = png.AsSpan(offset + 8, length).ToArray();
            offset += 12 + length;
        }

        Assert.NotNull(compressed);
        using var input = new MemoryStream(compressed!);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 255 }, raw.ToArray());
    }

    [Fact]
    public async Task CaptureWindowScreenshotAsync_InvalidHandle_ReturnsInvalidRequest()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var result = await executor.CaptureWindowScreenshotAsync(
            "not-a-handle",
            1920,
            1080,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }
}
