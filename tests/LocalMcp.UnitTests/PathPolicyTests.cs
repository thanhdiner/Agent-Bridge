using Microsoft.Extensions.Options;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.BuildingBlocks.Errors;

namespace LocalMcp.UnitTests;

public sealed class PathPolicyTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly PathPolicy _policy;

    public PathPolicyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_PathPolicyTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);

        var options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            DeniedSegments = new List<string> { ".git", ".ssh", "node_modules" },
            DeniedFileNames = new List<string> { ".env", "credentials.json" },
            MaxReadBytes = 100
        };

        _policy = new PathPolicy(Options.Create(options));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    [Fact]
    public void Validate_NullOrWhitespacePath_ReturnsInvalidPath()
    {
        var error = _policy.Validate("   ", out _);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.InvalidPath, error.Code);
    }

    [Fact]
    public void Validate_PathOutsideAllowedRoot_ReturnsPathOutsideAllowedRoot()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), "secret.txt");
        var error = _policy.Validate(outsidePath, out _);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.PathOutsideAllowedRoot, error.Code);
    }

    [Fact]
    public void Validate_PathTraversalEscapingRoot_ReturnsPathOutsideAllowedRoot()
    {
        var traversalPath = Path.Combine(_tempRoot, "..", "secret.txt");
        var error = _policy.Validate(traversalPath, out _);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.PathOutsideAllowedRoot, error.Code);
    }

    [Fact]
    public void Validate_PrefixCollisionSiblingFolder_ReturnsPathOutsideAllowedRoot()
    {
        var siblingPath = _tempRoot + "Fake" + Path.DirectorySeparatorChar + "secret.txt";
        var error = _policy.Validate(siblingPath, out _);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.PathOutsideAllowedRoot, error.Code);
    }

    [Fact]
    public void Validate_DeniedSegment_ReturnsAccessDenied()
    {
        var dirWithGit = Path.Combine(_tempRoot, ".git", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(dirWithGit)!);
        File.WriteAllText(dirWithGit, "config content");

        var error = _policy.Validate(dirWithGit, out _);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void Validate_DeniedFileName_ReturnsAccessDenied()
    {
        var envFilePath = Path.Combine(_tempRoot, ".env");
        File.WriteAllText(envFilePath, "secret=value");

        var error = _policy.Validate(envFilePath, out _);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void Validate_MissingFile_ReturnsFileNotFound()
    {
        var missingPath = Path.Combine(_tempRoot, "doesnotexist.txt");
        var error = _policy.Validate(missingPath, out _);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.FileNotFound, error.Code);
    }

    [Fact]
    public void Validate_FileExceedingMaxSize_ReturnsFileTooLarge()
    {
        var largeFilePath = Path.Combine(_tempRoot, "large.txt");
        File.WriteAllBytes(largeFilePath, new byte[150]);

        var error = _policy.Validate(largeFilePath, out _);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.FileTooLarge, error.Code);
    }

    [Fact]
    public void Validate_ValidFileInsideRoot_ReturnsNullErrorAndNormalizedPath()
    {
        var validFilePath = Path.Combine(_tempRoot, "readme.txt");
        File.WriteAllText(validFilePath, "Hello world");

        var error = _policy.Validate(validFilePath, out var normalizedPath);
        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(validFilePath), normalizedPath);
    }

    [Fact]
    public void Validate_SymlinkEscapingAllowedRoot_ReturnsPathOutsideAllowedRoot()
    {
        var targetFile = Path.Combine(Path.GetTempPath(), "escaped_target_" + Guid.NewGuid() + ".txt");
        File.WriteAllText(targetFile, "secret data");

        var linkPath = Path.Combine(_tempRoot, "escaping_link.txt");

        try
        {
            File.CreateSymbolicLink(linkPath, targetFile);
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            var error = _policy.Validate(linkPath, out _);
            Assert.NotNull(error);
            Assert.Equal(ErrorCodes.PathOutsideAllowedRoot, error.Code);
        }
        finally
        {
            try
            {
                if (File.Exists(linkPath)) File.Delete(linkPath);
                if (File.Exists(targetFile)) File.Delete(targetFile);
            }
            catch { }
        }
    }

    [Fact]
    public void Validate_ValidDirectoryInsideRoot_ReturnsNullErrorAndNormalizedPath()
    {
        var validDirPath = Path.Combine(_tempRoot, "subfolder");
        Directory.CreateDirectory(validDirPath);

        var error = _policy.Validate(validDirPath, out var normalizedPath, isDirectory: true);
        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(validDirPath), normalizedPath);
    }

    [Fact]
    public void Validate_DirectoryOutsideAllowedRoot_ReturnsPathOutsideAllowedRoot()
    {
        var outsideDirPath = Path.Combine(Path.GetTempPath(), "outside_subfolder_" + Guid.NewGuid());
        Directory.CreateDirectory(outsideDirPath);

        try
        {
            var error = _policy.Validate(outsideDirPath, out _, isDirectory: true);
            Assert.NotNull(error);
            Assert.Equal(ErrorCodes.PathOutsideAllowedRoot, error.Code);
        }
        finally
        {
            try
            {
                if (Directory.Exists(outsideDirPath)) Directory.Delete(outsideDirPath);
            }
            catch { }
        }
    }

    [Fact]
    public void Validate_DirectoryWithDeniedSegment_ReturnsAccessDenied()
    {
        var deniedDirPath = Path.Combine(_tempRoot, "node_modules", "somepkg");
        Directory.CreateDirectory(deniedDirPath);

        var error = _policy.Validate(deniedDirPath, out _, isDirectory: true);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void IsSubdirectoryOf_DriveRoot_AllowsChildPath()
    {
        Assert.True(PathPolicy.IsSubdirectoryOf(
            @"D:\mcp-scratch\a.txt",
            @"D:\"
        ));
    }
}
