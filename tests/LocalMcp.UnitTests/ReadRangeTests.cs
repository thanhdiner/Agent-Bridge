using System.Security.Cryptography;
using System.Text;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalMcp.UnitTests;

public sealed class ReadRangeTests : IDisposable
{
    private static readonly Encoding NoBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _tempRoot;
    private readonly FileAccessOptions _options;

    public ReadRangeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_ReadRangeTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);

        _options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            WritableRoots = new List<string> { _tempRoot },
            DeniedSegments = new List<string> { ".git", "bin", "obj" },
            DeniedFileNames = new List<string> { ".env" },
            DeniedWriteFileNames = new List<string>(),
            DeniedWriteExtensions = new List<string>(),
            MaxReadBytes = 1024,
            MaxWriteBytes = 1024
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private string WriteText(string name, string content)
    {
        var path = Path.Combine(_tempRoot, name);
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        File.WriteAllText(path, content, NoBomUtf8);
        return path;
    }

    private FileSystemExecutor MakeExecutor(FileAccessOptions? options = null)
    {
        var actualOptions = options ?? _options;
        var policy = new PathPolicy(Options.Create(actualOptions));
        return new FileSystemExecutor(
            policy,
            Options.Create(actualOptions),
            NullLogger<FileSystemExecutor>.Instance);
    }

    [Fact]
    public async Task ReadRangeAsync_RequestedSlice_ReturnsLinesAndMetadata()
    {
        var content = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line-{i}"));
        var path = WriteText("ten-lines.txt", content);
        var expectedHash = Convert.ToHexString(SHA256.HashData(NoBomUtf8.GetBytes(content))).ToLowerInvariant();

        var result = await MakeExecutor().ReadRangeAsync(
            path,
            startLine: 3,
            lineCount: 4,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(3L, result.Data!.StartLine);
        Assert.Equal(6L, result.Data.EndLine);
        Assert.Equal(10L, result.Data.TotalLines);
        Assert.Equal("line-3\nline-4\nline-5\nline-6", result.Data.Content);
        Assert.True(result.Data.Truncated);
        Assert.Equal(expectedHash, result.Data.Sha256);
        Assert.Equal("utf-8", result.Data.Encoding);
    }

    [Fact]
    public async Task ReadRangeAsync_StartBeyondEnd_ReturnsEmptyRange()
    {
        var path = WriteText("short.txt", "first\nsecond");

        var result = await MakeExecutor().ReadRangeAsync(
            path,
            startLine: 8,
            lineCount: 3,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2L, result.Data!.TotalLines);
        Assert.Equal(7L, result.Data.EndLine);
        Assert.Equal(string.Empty, result.Data.Content);
        Assert.False(result.Data.Truncated);
    }

    [Fact]
    public async Task ReadRangeAsync_FileLargerThanMaxReadBytes_SmallRangeStillSucceeds()
    {
        var content = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"row-{i:000}"));
        var path = WriteText("large.txt", content);
        var options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            WritableRoots = new List<string> { _tempRoot },
            DeniedSegments = new List<string>(),
            DeniedFileNames = new List<string>(),
            DeniedWriteFileNames = new List<string>(),
            DeniedWriteExtensions = new List<string>(),
            MaxReadBytes = 64,
            MaxWriteBytes = 1024
        };

        Assert.True(new FileInfo(path).Length > options.MaxReadBytes);

        var result = await MakeExecutor(options).ReadRangeAsync(
            path,
            startLine: 50,
            lineCount: 2,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("row-050\nrow-051", result.Data!.Content);
        Assert.Equal(200L, result.Data.TotalLines);
    }

    [Fact]
    public async Task ReadRangeAsync_MissingFile_ReturnsFileNotFound()
    {
        var path = Path.Combine(_tempRoot, "missing.txt");

        var result = await MakeExecutor().ReadRangeAsync(
            path,
            1,
            10,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FileNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task ReadRangeAsync_Directory_ReturnsAccessDenied()
    {
        var path = Path.Combine(_tempRoot, "directory");
        Directory.CreateDirectory(path);

        var result = await MakeExecutor().ReadRangeAsync(
            path,
            1,
            10,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.AccessDenied, result.Error!.Code);
    }

    [Theory]
    [InlineData(0L, 10)]
    [InlineData(-1L, 10)]
    [InlineData(1L, 0)]
    [InlineData(1L, 1001)]
    public async Task ReadRangeAsync_InvalidBounds_ReturnsInvalidRequest(long startLine, int lineCount)
    {
        var path = WriteText("bounds.txt", "content");

        var result = await MakeExecutor().ReadRangeAsync(
            path,
            startLine,
            lineCount,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error!.Code);
    }

    [Fact]
    public async Task ReadRangeAsync_BinaryFile_ReturnsBinaryNotSupported()
    {
        var path = Path.Combine(_tempRoot, "binary.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 0, 3, 4 });

        var result = await MakeExecutor().ReadRangeAsync(
            path,
            1,
            10,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.BinaryFileNotSupported, result.Error!.Code);
    }

    [Fact]
    public async Task ReadRangeAsync_InvalidUtf8_ReturnsUnsupportedEncoding()
    {
        var path = Path.Combine(_tempRoot, "invalid-utf8.txt");
        await File.WriteAllBytesAsync(path, new byte[] { 0xC3, 0x28, 0x0A });

        var result = await MakeExecutor().ReadRangeAsync(
            path,
            1,
            10,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.UnsupportedTextEncoding, result.Error!.Code);
    }

    [Fact]
    public async Task ReadRangeAsync_Utf16Bom_ReturnsUnsupportedEncoding()
    {
        var path = Path.Combine(_tempRoot, "utf16.txt");
        await File.WriteAllBytesAsync(path, new byte[] { 0xFF, 0xFE, 0x61, 0x00 });

        var result = await MakeExecutor().ReadRangeAsync(
            path,
            1,
            10,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.UnsupportedTextEncoding, result.Error!.Code);
    }

    [Fact]
    public async Task ReadRangeAsync_Utf8Bom_ReportsBomEncoding()
    {
        var path = Path.Combine(_tempRoot, "bom.txt");
        var payload = NoBomUtf8.GetBytes("alpha\nbeta");
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray();
        await File.WriteAllBytesAsync(path, bytes);

        var result = await MakeExecutor().ReadRangeAsync(
            path,
            1,
            2,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("alpha\nbeta", result.Data!.Content);
        Assert.Equal("utf-8-bom", result.Data.Encoding);
    }

    [Fact]
    public async Task ReadRangeAsync_DeniedSegment_ReturnsAccessDenied()
    {
        var path = WriteText(Path.Combine(".git", "config"), "secret");

        var result = await MakeExecutor().ReadRangeAsync(
            path,
            1,
            10,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.AccessDenied, result.Error!.Code);
    }

    [Fact]
    public async Task ReadRangeAsync_SelectedContentExceedsResponseLimit_ReturnsFileTooLarge()
    {
        var path = WriteText("long-line.txt", new string('x', 100));
        var options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            WritableRoots = new List<string> { _tempRoot },
            DeniedSegments = new List<string>(),
            DeniedFileNames = new List<string>(),
            DeniedWriteFileNames = new List<string>(),
            DeniedWriteExtensions = new List<string>(),
            MaxReadBytes = 32,
            MaxWriteBytes = 1024
        };

        var result = await MakeExecutor(options).ReadRangeAsync(
            path,
            1,
            1,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FileTooLarge, result.Error!.Code);
    }
}
