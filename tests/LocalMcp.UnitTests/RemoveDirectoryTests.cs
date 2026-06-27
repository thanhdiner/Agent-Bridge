using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalMcp.UnitTests;

public sealed class RemoveDirectoryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _allowedButNotWritableRoot;
    private readonly FileAccessOptions _options;

    public RemoveDirectoryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_RemoveDirectoryTests_" + Guid.NewGuid());
        _allowedButNotWritableRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_RemoveDirectoryReadOnlyTests_" + Guid.NewGuid());
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

    private string TempDir(string relativePath)
    {
        var path = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    private PathPolicy MakePolicy() => new(Options.Create(_options));

    private FileSystemExecutor MakeExecutor() =>
        new(
            MakePolicy(),
            Options.Create(_options),
            NullLogger<FileSystemExecutor>.Instance);

    [Fact]
    public void AuthorizeRemoveDirectory_EmptyDirectory_Succeeds()
    {
        var path = TempDir("empty");

        var error = MakePolicy().AuthorizeRemoveDirectory(path, missingOk: false, out var normalizedPath);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(path), normalizedPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizeRemoveDirectory_MissingDirectory_ReturnsDirectoryNotFound()
    {
        var path = Path.Combine(_tempRoot, "missing");

        var error = MakePolicy().AuthorizeRemoveDirectory(path, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.DirectoryNotFound, error!.Code);
    }

    [Fact]
    public void AuthorizeRemoveDirectory_MissingDirectoryWithMissingOk_Succeeds()
    {
        var path = Path.Combine(_tempRoot, "missing-ok");

        var error = MakePolicy().AuthorizeRemoveDirectory(path, missingOk: true, out var normalizedPath);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(path), normalizedPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizeRemoveDirectory_FilePath_ReturnsAccessDenied()
    {
        var path = Path.Combine(_tempRoot, "file.txt");
        File.WriteAllText(path, "data");

        var error = MakePolicy().AuthorizeRemoveDirectory(path, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public void AuthorizeRemoveDirectory_OutsideWritableRoot_ReturnsWriteNotAllowed()
    {
        var path = Path.Combine(_allowedButNotWritableRoot, "empty");
        Directory.CreateDirectory(path);

        var error = MakePolicy().AuthorizeRemoveDirectory(path, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.WriteNotAllowed, error!.Code);
    }

    [Fact]
    public void AuthorizeRemoveDirectory_ConfiguredRoot_ReturnsAccessDenied()
    {
        var error = MakePolicy().AuthorizeRemoveDirectory(_tempRoot, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public void AuthorizeRemoveDirectory_ConfiguredRootWithTrailingSeparator_IsBlocked()
    {
        var options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot + Path.DirectorySeparatorChar },
            WritableRoots = new List<string> { _tempRoot + Path.DirectorySeparatorChar }
        };
        var policy = new PathPolicy(Options.Create(options));

        var error = policy.AuthorizeRemoveDirectory(_tempRoot, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.True(error!.Code == ErrorCodes.AccessDenied || error.Code == ErrorCodes.WriteNotAllowed);
    }

    [Fact]
    public void AuthorizeRemoveDirectory_DeniedSegment_ReturnsAccessDenied()
    {
        var path = TempDir(Path.Combine(".git", "empty"));

        var error = MakePolicy().AuthorizeRemoveDirectory(path, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Theory]
    [InlineData("secret.txt")]
    [InlineData(".env")]
    public void AuthorizeRemoveDirectory_DeniedDirectoryName_ReturnsAccessDenied(string directoryName)
    {
        var path = TempDir(directoryName);

        var error = MakePolicy().AuthorizeRemoveDirectory(path, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public void AuthorizeRemoveDirectory_DirectorySymlink_ReturnsAccessDenied()
    {
        var target = TempDir("target");
        var link = Path.Combine(_tempRoot, "target-link");

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var error = MakePolicy().AuthorizeRemoveDirectory(link, missingOk: false, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public void AuthorizeRemoveDirectory_ReparsePointInParent_ReturnsAccessDenied()
    {
        var target = TempDir("parent-target");
        Directory.CreateDirectory(Path.Combine(target, "child"));
        var link = Path.Combine(_tempRoot, "parent-link");

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var error = MakePolicy().AuthorizeRemoveDirectory(
            Path.Combine(link, "child"),
            missingOk: false,
            out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error!.Code);
    }

    [Fact]
    public async Task RemoveDirectoryAsync_EmptyDirectory_RemovesSuccessfully()
    {
        var path = TempDir("remove-empty");

        var result = await MakeExecutor().RemoveDirectoryAsync(
            path,
            missingOk: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.Removed);
        Assert.Equal(Path.GetFullPath(path), result.Data.Path, StringComparer.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task RemoveDirectoryAsync_DirectoryContainingFile_ReturnsDirectoryNotEmpty()
    {
        var path = TempDir("contains-file");
        File.WriteAllText(Path.Combine(path, "child.txt"), "data");

        var result = await MakeExecutor().RemoveDirectoryAsync(
            path,
            missingOk: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.DirectoryNotEmpty, result.Error!.Code);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task RemoveDirectoryAsync_DirectoryContainingHiddenFile_ReturnsDirectoryNotEmpty()
    {
        var path = TempDir("contains-hidden-file");
        var child = Path.Combine(path, ".hidden.txt");
        File.WriteAllText(child, "data");
        try { File.SetAttributes(child, File.GetAttributes(child) | FileAttributes.Hidden); } catch { }

        var result = await MakeExecutor().RemoveDirectoryAsync(
            path,
            missingOk: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.DirectoryNotEmpty, result.Error!.Code);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task RemoveDirectoryAsync_DirectoryContainingSubdirectory_ReturnsDirectoryNotEmpty()
    {
        var path = TempDir(Path.Combine("contains-directory", "child"));
        var parent = Directory.GetParent(path)!.FullName;

        var result = await MakeExecutor().RemoveDirectoryAsync(
            parent,
            missingOk: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.DirectoryNotEmpty, result.Error!.Code);
        Assert.True(Directory.Exists(parent));
    }

    [Fact]
    public async Task RemoveDirectoryAsync_MissingDirectoryWithMissingOk_ReturnsNotRemoved()
    {
        var path = Path.Combine(_tempRoot, "already-gone");

        var result = await MakeExecutor().RemoveDirectoryAsync(
            path,
            missingOk: true,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(result.Data!.Removed);
    }

    [Fact]
    public async Task RemoveDirectoryAsync_MissingWithoutMissingOk_ReturnsDirectoryNotFound()
    {
        var path = Path.Combine(_tempRoot, "missing-executor");
        var result = await MakeExecutor().RemoveDirectoryAsync(
            path,
            false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.DirectoryNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task RemoveDirectoryAsync_ChildAppearsBeforeDelete_ReturnsDirectoryNotEmpty()
    {
        var path = TempDir("concurrent-child");
        var executor = MakeExecutor();
        executor.OnBeforeDirectoryDeleteHook = directoryPath =>
        {
            File.WriteAllText(Path.Combine(directoryPath, "appeared.txt"), "data");
            return Task.CompletedTask;
        };

        var result = await executor.RemoveDirectoryAsync(
            path,
            missingOk: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.DirectoryNotEmpty, result.Error!.Code);
        Assert.True(Directory.Exists(path));
    }
}
