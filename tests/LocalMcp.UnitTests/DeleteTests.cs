using System.Security.Cryptography;
using System.Text;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalMcp.UnitTests;

public sealed class DeleteTests : IDisposable
{
    private static readonly Encoding NoBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _tempRoot;
    private readonly string _allowedButNotWritableRoot;
    private readonly FileAccessOptions _options;

    public DeleteTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_DeleteTests_" + Guid.NewGuid());
        _allowedButNotWritableRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_DeleteReadOnlyTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_allowedButNotWritableRoot);

        _options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot, _allowedButNotWritableRoot },
            WritableRoots = new List<string> { _tempRoot },
            DeniedSegments = new List<string> { ".git", "bin", "obj" },
            DeniedFileNames = new List<string> { "secret.txt" },
            DeniedWriteFileNames = new List<string> { ".env", "id_rsa" },
            DeniedWriteExtensions = new List<string> { ".pem", ".key" },
            MaxReadBytes = 2_097_152,
            MaxWriteBytes = 524_288
        };
    }

    public void Dispose()
    {
        CleanupRoot(_tempRoot);
        CleanupRoot(_allowedButNotWritableRoot);
    }

    private static void CleanupRoot(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }

            Directory.Delete(root, recursive: true);
        }
        catch { }
    }

    private string TempFile(string name, string content = "delete-me")
    {
        var path = Path.Combine(_tempRoot, name);
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        File.WriteAllText(path, content, NoBomUtf8);
        return path;
    }

    private static string Sha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(NoBomUtf8.GetBytes(content))).ToLowerInvariant();

    private PathPolicy MakePolicy() => new(Options.Create(_options));

    private FileSystemExecutor MakeExecutor() =>
        new(
            MakePolicy(),
            Options.Create(_options),
            NullLogger<FileSystemExecutor>.Instance);

    [Fact]
    public void AuthorizeDeleteFile_ValidFile_Succeeds()
    {
        var path = TempFile("valid.txt");

        var error = MakePolicy().AuthorizeDeleteFile(path, missingOk: false, out var normalizedPath);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(path), normalizedPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizeDeleteFile_MissingFile_ReturnsFileNotFound()
    {
        var path = Path.Combine(_tempRoot, "missing.txt");

        var error = MakePolicy().AuthorizeDeleteFile(path, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.FileNotFound, error!.Code);
    }

    [Fact]
    public void AuthorizeDeleteFile_MissingFileWithMissingOk_Succeeds()
    {
        var path = Path.Combine(_tempRoot, "missing.txt");

        var error = MakePolicy().AuthorizeDeleteFile(path, missingOk: true, out var normalizedPath);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(path), normalizedPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizeDeleteFile_Directory_ReturnsAccessDenied()
    {
        var path = Path.Combine(_tempRoot, "directory");
        Directory.CreateDirectory(path);

        var error = MakePolicy().AuthorizeDeleteFile(path, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public void AuthorizeDeleteFile_OutsideWritableRoot_ReturnsWriteNotAllowed()
    {
        var path = Path.Combine(_allowedButNotWritableRoot, "read-only-root.txt");
        File.WriteAllText(path, "data", NoBomUtf8);

        var error = MakePolicy().AuthorizeDeleteFile(path, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.WriteNotAllowed, error!.Code);
    }

    [Fact]
    public void AuthorizeDeleteFile_ReadOnlyFile_ReturnsFileReadOnly()
    {
        var path = TempFile("readonly.txt");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        var error = MakePolicy().AuthorizeDeleteFile(path, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.FileReadOnly, error!.Code);
    }

    [Theory]
    [InlineData("secret.txt")]
    [InlineData(".env")]
    [InlineData("private.pem")]
    public void AuthorizeDeleteFile_DeniedFile_ReturnsAccessDenied(string fileName)
    {
        var path = TempFile(fileName);

        var error = MakePolicy().AuthorizeDeleteFile(path, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public void AuthorizeDeleteFile_DeniedSegment_ReturnsAccessDenied()
    {
        var path = TempFile(Path.Combine(".git", "config"));

        var error = MakePolicy().AuthorizeDeleteFile(path, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public void AuthorizeDeleteFile_FileSymlink_ReturnsAccessDenied()
    {
        var target = TempFile("target.txt");
        var link = Path.Combine(_tempRoot, "target-link.txt");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var error = MakePolicy().AuthorizeDeleteFile(link, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public void AuthorizeDeleteFile_ReparsePointInParent_ReturnsAccessDenied()
    {
        var targetDirectory = Path.Combine(_tempRoot, "target-directory");
        Directory.CreateDirectory(targetDirectory);
        File.WriteAllText(Path.Combine(targetDirectory, "child.txt"), "data", NoBomUtf8);
        var linkDirectory = Path.Combine(_tempRoot, "linked-directory");

        try
        {
            Directory.CreateSymbolicLink(linkDirectory, targetDirectory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var error = MakePolicy().AuthorizeDeleteFile(
            Path.Combine(linkDirectory, "child.txt"),
            missingOk: false,
            out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public async Task DeleteAsync_ExistingFile_DeletesAndReturnsMetadata()
    {
        const string content = "delete-this-content";
        var path = TempFile("delete.txt", content);
        var expectedHash = Sha256Hex(content);

        var result = await MakeExecutor().DeleteAsync(
            path,
            expectedHash,
            missingOk: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(File.Exists(path));
        Assert.Equal((long)NoBomUtf8.GetByteCount(content), result.Data!.BytesDeleted);
        Assert.Equal(expectedHash, result.Data.Sha256);
        Assert.Equal(Path.GetFullPath(path), result.Data.Path, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAsync_WrongExpectedSha256_ReturnsConcurrencyConflictAndPreservesFile()
    {
        var path = TempFile("hash-mismatch.txt", "current-content");

        var result = await MakeExecutor().DeleteAsync(
            path,
            "deadbeef",
            missingOk: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ConcurrencyConflict, result.Error!.Code);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DeleteAsync_MissingFileWithMissingOk_ReturnsSuccess()
    {
        var path = Path.Combine(_tempRoot, "already-gone.txt");

        var result = await MakeExecutor().DeleteAsync(
            path,
            expectedSha256: null,
            missingOk: true,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(0, result.Data!.BytesDeleted);
        Assert.Null(result.Data.Sha256);
    }
}
