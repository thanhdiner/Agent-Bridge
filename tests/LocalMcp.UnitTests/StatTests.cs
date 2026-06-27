using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Contracts.Commands;
using LocalMcp.BuildingBlocks.Errors;

namespace LocalMcp.UnitTests;

public sealed class StatTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileAccessOptions _options;

    public StatTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_StatTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);

        _options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            WritableRoots = new List<string> { _tempRoot },
            DeniedSegments = new List<string> { ".git", "bin" },
            DeniedFileNames = new List<string> { "secret.txt" },
            DeniedWriteFileNames = new List<string> { ".env" },
            DeniedWriteExtensions = new List<string> { ".pem" },
            MaxReadBytes = 100, // 100 bytes limit for test
            MaxWriteBytes = 1000
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void PathPolicy_Stat_NonExistentFile_Succeeds()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var rawPath = Path.Combine(_tempRoot, "missing.txt");

        var error = policy.AuthorizeStat(rawPath, out var normalizedPath);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(rawPath), normalizedPath);
    }

    [Fact]
    public void PathPolicy_Stat_DeniedSegment_ReturnsAccessDenied()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var rawPath = Path.Combine(_tempRoot, "bin", "config.json");

        var error = policy.AuthorizeStat(rawPath, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void PathPolicy_Stat_OutsideAllowedRoot_ReturnsPathOutsideAllowedRoot()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var rawPath = Path.Combine(Path.GetTempPath(), "outside.txt");

        var error = policy.AuthorizeStat(rawPath, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.PathOutsideAllowedRoot, error.Code);
    }

    [Fact]
    public async Task Executor_Stat_FileExists_ReturnsFileMetadata()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "test.txt");
        var text = "Hello from stat test!";
        await File.WriteAllTextAsync(target, text);

        var result = await executor.StatAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Exists);
        Assert.Equal("file", result.Data.Type);
        Assert.Equal(text.Length, result.Data.Size);
        Assert.Equal("utf-8", result.Data.Encoding);
        Assert.NotNull(result.Data.Sha256);
        Assert.False(result.Data.ReadOnly);
        Assert.NotNull(result.Data.LastWriteTimeUtc);
        Assert.False(result.Data.IsReparsePoint);
        Assert.True(result.Data.ContentMetadataAvailable);
        Assert.False(result.Data.ContentMetadataSkipped);
        Assert.Null(result.Data.ContentMetadataErrorCode);
    }

    [Fact]
    public async Task Executor_Stat_DirectoryExists_ReturnsDirectoryMetadata()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "sub_dir");
        Directory.CreateDirectory(target);

        var result = await executor.StatAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Exists);
        Assert.Equal("directory", result.Data.Type);
        Assert.Null(result.Data.Size);
        Assert.Null(result.Data.Encoding);
        Assert.Null(result.Data.Sha256);
        Assert.False(result.Data.ReadOnly);
        Assert.NotNull(result.Data.LastWriteTimeUtc);
        Assert.False(result.Data.IsReparsePoint);
        Assert.False(result.Data.ContentMetadataAvailable);
        Assert.False(result.Data.ContentMetadataSkipped);
    }

    [Fact]
    public async Task Executor_Stat_NotExists_ReturnsExistsFalse()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "non_existent.txt");

        var result = await executor.StatAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(result.Data.Exists);
        Assert.Null(result.Data.Type);
    }

    [Fact]
    public async Task Executor_Stat_SmallUtf8BomFile_Succeeds()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "bom.txt");

        var contentBytes = new List<byte> { 0xEF, 0xBB, 0xBF };
        contentBytes.AddRange(Encoding.UTF8.GetBytes("BOM test content"));
        await File.WriteAllBytesAsync(target, contentBytes.ToArray());

        var result = await executor.StatAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("utf-8-bom", result.Data.Encoding);
        Assert.True(result.Data.ContentMetadataAvailable);
        Assert.False(result.Data.ContentMetadataSkipped);
        Assert.NotNull(result.Data.Sha256);
    }

    [Fact]
    public async Task Executor_Stat_SmallBinaryFile_SucceedsWithNullEncoding()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "binary.bin");

        var binaryBytes = new byte[] { 0, 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(target, binaryBytes);

        var result = await executor.StatAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Null(result.Data.Encoding);
        Assert.True(result.Data.ContentMetadataAvailable);
        Assert.False(result.Data.ContentMetadataSkipped);
        Assert.NotNull(result.Data.Sha256);
    }

    [Fact]
    public async Task Executor_Stat_InvalidUtf8File_SucceedsWithNullEncoding()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "invalid_utf8.txt");

        // 0xC0 0xAF is invalid UTF-8 sequence
        var invalidBytes = new byte[] { 0xC0, 0xAF, 0x61, 0x62, 0x63 };
        await File.WriteAllBytesAsync(target, invalidBytes);

        var result = await executor.StatAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Null(result.Data.Encoding);
        Assert.True(result.Data.ContentMetadataAvailable);
        Assert.False(result.Data.ContentMetadataSkipped);
        Assert.NotNull(result.Data.Sha256);
    }

    [Fact]
    public async Task Executor_Stat_OversizedFile_SkipsContentMetadata()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "large.txt");

        // Write 150 bytes, which exceeds _options.MaxReadBytes (100)
        var largeBytes = new byte[150];
        new Random().NextBytes(largeBytes);
        await File.WriteAllBytesAsync(target, largeBytes);

        var result = await executor.StatAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Exists);
        Assert.True(result.Data.ContentMetadataSkipped);
        Assert.False(result.Data.ContentMetadataAvailable);
        Assert.Null(result.Data.Sha256);
        Assert.Null(result.Data.Encoding);
        Assert.Equal(150, result.Data.Size);
    }

    [Fact]
    public async Task Executor_Stat_LockedFile_HandlesGracefully()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "locked.txt");
        await File.WriteAllTextAsync(target, "some text");

        // Keep it open with exclusive write lock (FileShare.None)
        using var stream = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await executor.StatAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Exists);
        Assert.False(result.Data.ContentMetadataAvailable);
        Assert.False(result.Data.ContentMetadataSkipped);
        Assert.Equal("IO_ERROR", result.Data.ContentMetadataErrorCode);
        Assert.Null(result.Data.Sha256);
        Assert.Null(result.Data.Encoding);
        Assert.Equal(9, result.Data.Size);
    }

    [Fact]
    public async Task Executor_Stat_FileGrowsBetweenChecks_SkipsContentMetadataGracefully()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "racy_grow.txt");

        // Initial size: 50 bytes (below limit of 100)
        var initialBytes = new byte[50];
        await File.WriteAllBytesAsync(target, initialBytes);

        // Before content is read, grow file to 150 bytes (above limit of 100)
        executor.OnBeforeContentReadHook = async (path) =>
        {
            var growBytes = new byte[150];
            await File.WriteAllBytesAsync(path, growBytes);
        };

        var result = await executor.StatAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Exists);
        // It should detect the file has grown beyond MaxReadBytes and skip metadata
        Assert.True(result.Data.ContentMetadataSkipped);
        Assert.False(result.Data.ContentMetadataAvailable);
        Assert.Null(result.Data.Sha256);
        Assert.Null(result.Data.Encoding);
    }
}
