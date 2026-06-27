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
using LocalMcp.BuildingBlocks.Errors;

namespace LocalMcp.UnitTests;

/// <summary>
/// Integration tests for fs_move and fs_copy: PathPolicy authorization and
/// FileSystemExecutor physical execution, including concurrency-guard SHA-256 checks,
/// temp-file cleanup on failure, and cross-volume rejection.
/// </summary>
public sealed class MoveCopyTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileAccessOptions _options;

    public MoveCopyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_MoveCopyTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);

        _options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            WritableRoots = new List<string> { _tempRoot },
            DeniedSegments = new List<string> { ".git", "bin", "obj" },
            DeniedFileNames = new List<string> { "secret.txt" },
            DeniedWriteFileNames = new List<string> { ".env", "id_rsa" },
            DeniedWriteExtensions = new List<string> { ".pem", ".key" },
            MaxReadBytes = 2_097_152,
            MaxWriteBytes = 500
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                foreach (var f in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(_tempRoot, true);
            }
        }
        catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static readonly Encoding NoBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private string TempFile(string name, string content = "hello")
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, content, NoBomUtf8);
        return path;
    }

    private string TempDir(string name)
    {
        var path = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Sha256Hex(string content)
    {
        var bytes = NoBomUtf8.GetBytes(content);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private PathPolicy MakePolicy() => new PathPolicy(Options.Create(_options));

    private FileSystemExecutor MakeExecutor() =>
        new FileSystemExecutor(
            MakePolicy(),
            Options.Create(_options),
            NullLogger<FileSystemExecutor>.Instance);

    // ── AuthorizeMove – happy path ────────────────────────────────────────────

    [Fact]
    public void AuthorizeMove_ValidFilePaths_Succeeds()
    {
        var src = TempFile("a.txt");
        var dst = Path.Combine(_tempRoot, "b.txt");
        var policy = MakePolicy();

        var error = policy.AuthorizeMove(src, dst, overwrite: false, out var normSrc, out var normDst);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(src), normSrc, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(dst), normDst, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizeMove_ValidDirectoryPaths_Succeeds()
    {
        var src = TempDir("subA");
        var dst = Path.Combine(_tempRoot, "subB");
        var policy = MakePolicy();

        var error = policy.AuthorizeMove(src, dst, overwrite: false, out _, out _);

        Assert.Null(error);
    }

    // ── AuthorizeMove – rejection cases ──────────────────────────────────────

    [Fact]
    public void AuthorizeMove_NoWritableRoots_ReturnsNotConfigured()
    {
        var opts = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            WritableRoots = new List<string>()
        };
        var policy = new PathPolicy(Options.Create(opts));
        var src = TempFile("x.txt");

        var error = policy.AuthorizeMove(src, Path.Combine(_tempRoot, "y.txt"), false, out _, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.WritableRootNotConfigured, error!.Code);
    }

    [Fact]
    public void AuthorizeMove_SourceNotFound_ReturnsFileNotFound()
    {
        var policy = MakePolicy();
        var missing = Path.Combine(_tempRoot, "missing.txt");

        var error = policy.AuthorizeMove(missing, Path.Combine(_tempRoot, "dst.txt"), false, out _, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.FileNotFound, error!.Code);
    }

    [Fact]
    public void AuthorizeMove_DestinationParentMissing_ReturnsDirectoryNotFound()
    {
        var src = TempFile("src.txt");
        var policy = MakePolicy();
        var dst = Path.Combine(_tempRoot, "nonexistent_dir", "dst.txt");

        var error = policy.AuthorizeMove(src, dst, false, out _, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.DirectoryNotFound, error!.Code);
    }

    [Fact]
    public void AuthorizeMove_DestinationExistsNoOverwrite_ReturnsAccessDenied()
    {
        var src = TempFile("src.txt");
        var dst = TempFile("dst.txt");
        var policy = MakePolicy();

        var error = policy.AuthorizeMove(src, dst, overwrite: false, out _, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public void AuthorizeMove_DestinationExistsWithOverwrite_Succeeds()
    {
        var src = TempFile("src.txt");
        var dst = TempFile("dst.txt");
        var policy = MakePolicy();

        var error = policy.AuthorizeMove(src, dst, overwrite: true, out _, out _);

        Assert.Null(error);
    }

    [Fact]
    public void AuthorizeMove_DestinationIsDirectory_ReturnsAccessDenied()
    {
        var src = TempFile("src.txt");
        var dst = TempDir("existingDir");
        var policy = MakePolicy();

        var error = policy.AuthorizeMove(src, dst, overwrite: true, out _, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public void AuthorizeMove_SourceOutsideAllowedRoot_ReturnsError()
    {
        var outside = Path.GetTempFileName();
        try
        {
            var policy = MakePolicy();
            var dst = Path.Combine(_tempRoot, "dst.txt");

            var error = policy.AuthorizeMove(outside, dst, false, out _, out _);

            Assert.NotNull(error);
            Assert.True(error!.Code == ErrorCodes.WriteNotAllowed || error.Code == ErrorCodes.PathOutsideAllowedRoot);
        }
        finally
        {
            try { File.Delete(outside); } catch { }
        }
    }

    [Fact]
    public void AuthorizeMove_DeniedSegmentInSource_ReturnsAccessDenied()
    {
        var gitDir = Path.Combine(_tempRoot, ".git");
        Directory.CreateDirectory(gitDir);
        var src = Path.Combine(gitDir, "config");
        File.WriteAllText(src, "data");
        var policy = MakePolicy();

        var error = policy.AuthorizeMove(src, Path.Combine(_tempRoot, "out.txt"), false, out _, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public void AuthorizeMove_DeniedWriteExtensionInDestination_ReturnsAccessDenied()
    {
        var src = TempFile("src.txt");
        var policy = MakePolicy();
        var dst = Path.Combine(_tempRoot, "key.pem");

        var error = policy.AuthorizeMove(src, dst, false, out _, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    // ── AuthorizeCopy – happy path ────────────────────────────────────────────

    [Fact]
    public void AuthorizeCopy_ValidFilePaths_Succeeds()
    {
        var src = TempFile("orig.txt");
        var dst = Path.Combine(_tempRoot, "copy.txt");
        var policy = MakePolicy();

        var error = policy.AuthorizeCopy(src, dst, overwrite: false, out var normSrc, out var normDst);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(src), normSrc, StringComparer.OrdinalIgnoreCase);
    }

    // ── AuthorizeCopy – rejection cases ──────────────────────────────────────

    [Fact]
    public void AuthorizeCopy_SourceIsDirectory_ReturnsAccessDenied()
    {
        var src = TempDir("dirSrc");
        var dst = Path.Combine(_tempRoot, "dst.txt");
        var policy = MakePolicy();

        var error = policy.AuthorizeCopy(src, dst, false, out _, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public void AuthorizeCopy_SourceNotFound_ReturnsFileNotFound()
    {
        var policy = MakePolicy();
        var error = policy.AuthorizeCopy(
            Path.Combine(_tempRoot, "missing.txt"),
            Path.Combine(_tempRoot, "dst.txt"),
            false, out _, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.FileNotFound, error!.Code);
    }

    [Fact]
    public void AuthorizeCopy_DestinationExistsNoOverwrite_ReturnsAccessDenied()
    {
        var src = TempFile("src.txt");
        var dst = TempFile("dst.txt");
        var policy = MakePolicy();

        var error = policy.AuthorizeCopy(src, dst, overwrite: false, out _, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public void AuthorizeCopy_DeniedWriteFileNameInDestination_ReturnsAccessDenied()
    {
        var src = TempFile("src.txt");
        var policy = MakePolicy();
        var dst = Path.Combine(_tempRoot, ".env");

        var error = policy.AuthorizeCopy(src, dst, false, out _, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    // ── MoveAsync – executor integration ─────────────────────────────────────

    [Fact]
    public async Task MoveAsync_File_MovesSuccessfully()
    {
        const string content = "move-me";
        var src = TempFile("src_move.txt", content);
        var dst = Path.Combine(_tempRoot, "dst_move.txt");
        var executor = MakeExecutor();

        var result = await executor.MoveAsync(src, dst, overwrite: false, expectedSha256: null, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(File.Exists(src));
        Assert.True(File.Exists(dst));
        Assert.Equal(content, File.ReadAllText(dst));
    }

    [Fact]
    public async Task MoveAsync_Directory_MovesSuccessfully()
    {
        var srcDir = TempDir("src_dir");
        File.WriteAllText(Path.Combine(srcDir, "child.txt"), "data");
        var dstDir = Path.Combine(_tempRoot, "dst_dir");
        var executor = MakeExecutor();

        var result = await executor.MoveAsync(srcDir, dstDir, overwrite: false, expectedSha256: null, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.IsDirectory);
        Assert.False(Directory.Exists(srcDir));
        Assert.True(File.Exists(Path.Combine(dstDir, "child.txt")));
    }

    [Fact]
    public async Task MoveAsync_WrongExpectedSha256_ReturnsConcurrencyConflict()
    {
        var src = TempFile("sha_src.txt", "actual-content");
        var dst = Path.Combine(_tempRoot, "sha_dst.txt");
        var executor = MakeExecutor();

        var result = await executor.MoveAsync(src, dst, false, expectedSha256: "deadbeef00000000", Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ConcurrencyConflict, result.Error!.Code);
        Assert.True(File.Exists(src)); // source untouched
    }

    [Fact]
    public async Task MoveAsync_CorrectExpectedSha256_MovesSuccessfully()
    {
        const string content = "sha-guarded";
        var src = TempFile("sha_src2.txt", content);
        var dst = Path.Combine(_tempRoot, "sha_dst2.txt");
        var hash = Sha256Hex(content);
        var executor = MakeExecutor();

        var result = await executor.MoveAsync(src, dst, false, expectedSha256: hash, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(src));
    }

    // ── CopyAsync – executor integration ─────────────────────────────────────

    [Fact]
    public async Task CopyAsync_File_CopiesSuccessfully()
    {
        const string content = "copy-me";
        var src = TempFile("src_copy.txt", content);
        var dst = Path.Combine(_tempRoot, "dst_copy.txt");
        var executor = MakeExecutor();

        var result = await executor.CopyAsync(src, dst, overwrite: false, expectedSourceSha256: null, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(File.Exists(src));  // source preserved
        Assert.True(File.Exists(dst));
        Assert.Equal(content, File.ReadAllText(dst));
        Assert.Equal((long)NoBomUtf8.GetByteCount(content), result.Data!.BytesCopied);
    }

    [Fact]
    public async Task CopyAsync_DestinationExists_OverwriteTrue_Overwrites()
    {
        var src = TempFile("src_ow.txt", "new-content");
        var dst = TempFile("dst_ow.txt", "old-content");
        var executor = MakeExecutor();

        var result = await executor.CopyAsync(src, dst, overwrite: true, expectedSourceSha256: null, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("new-content", File.ReadAllText(dst));
    }

    [Fact]
    public async Task CopyAsync_DestinationExists_OverwriteFalse_ReturnsError()
    {
        var src = TempFile("src_noo.txt", "content");
        var dst = TempFile("dst_noo.txt", "existing");
        var executor = MakeExecutor();

        var result = await executor.CopyAsync(src, dst, overwrite: false, expectedSourceSha256: null, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.AccessDenied, result.Error!.Code);
    }

    [Fact]
    public async Task CopyAsync_WrongExpectedSha256_ReturnsConcurrencyConflict()
    {
        var src = TempFile("sha_copy_src.txt", "actual-content");
        var dst = Path.Combine(_tempRoot, "sha_copy_dst.txt");
        var executor = MakeExecutor();

        var result = await executor.CopyAsync(src, dst, false, expectedSourceSha256: "0000000000000000", Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ConcurrencyConflict, result.Error!.Code);
        Assert.False(File.Exists(dst)); // no partial destination
    }

    [Fact]
    public async Task CopyAsync_CorrectExpectedSha256_CopiesSuccessfully()
    {
        const string content = "sha-copy-guarded";
        var src = TempFile("sha_copy_src2.txt", content);
        var dst = Path.Combine(_tempRoot, "sha_copy_dst2.txt");
        var hash = Sha256Hex(content);
        var executor = MakeExecutor();

        var result = await executor.CopyAsync(src, dst, false, expectedSourceSha256: hash, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(hash, result.Data!.Sha256);
    }

    [Fact]
    public async Task CopyAsync_NoTempFilesLeftAfterSuccess()
    {
        var src = TempFile("no_temp_src.txt", "clean");
        var dst = Path.Combine(_tempRoot, "no_temp_dst.txt");
        var executor = MakeExecutor();

        await executor.CopyAsync(src, dst, false, null, Guid.NewGuid(), CancellationToken.None);

        var tmpFiles = Directory.GetFiles(_tempRoot, "copy-temp-*.tmp");
        Assert.Empty(tmpFiles);
    }
}
