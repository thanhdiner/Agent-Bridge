using Microsoft.Extensions.Options;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.BuildingBlocks.Errors;

namespace LocalMcp.UnitTests;

/// <summary>
/// Additional PathPolicy tests covering:
/// - bin/obj denied traversal (Task 1)
/// - Case-insensitive denied segment matching (Task 1)
/// - Normal source directory remains visible (Task 1)
/// - FILE_NOT_FOUND vs DIRECTORY_NOT_FOUND distinction (Task 4)
/// </summary>
public sealed class PathPolicyTraversalTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly PathPolicy _policy;

    public PathPolicyTraversalTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_TraversalTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);

        var options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            DeniedSegments = new List<string>
            {
                "bin", "obj", ".git", ".ssh", ".vs", ".idea", "node_modules"
            },
            DeniedFileNames = new List<string> { ".env" },
            MaxReadBytes = 1024 * 1024
        };

        _policy = new PathPolicy(Options.Create(options));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, true);
        }
        catch { }
    }

    // ── Task 1: Denied segment traversal ──────────────────────────────────────

    [Fact]
    public void Validate_BinDirectory_IsAccessDenied()
    {
        var binDir = Path.Combine(_tempRoot, "bin");
        Directory.CreateDirectory(binDir);

        var error = _policy.Validate(binDir, out _, isDirectory: true);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void Validate_ObjDirectory_IsAccessDenied()
    {
        var objDir = Path.Combine(_tempRoot, "obj");
        Directory.CreateDirectory(objDir);

        var error = _policy.Validate(objDir, out _, isDirectory: true);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void Validate_GitDirectory_IsAccessDenied()
    {
        var gitDir = Path.Combine(_tempRoot, ".git");
        Directory.CreateDirectory(gitDir);

        var error = _policy.Validate(gitDir, out _, isDirectory: true);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void Validate_NodeModulesDirectory_IsAccessDenied()
    {
        var nmDir = Path.Combine(_tempRoot, "node_modules");
        Directory.CreateDirectory(nmDir);

        var error = _policy.Validate(nmDir, out _, isDirectory: true);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void Validate_FileInsideBinDirectory_IsAccessDenied()
    {
        var binDir = Path.Combine(_tempRoot, "bin", "Release");
        Directory.CreateDirectory(binDir);
        var filePath = Path.Combine(binDir, "App.dll");
        File.WriteAllText(filePath, "binary");

        var error = _policy.Validate(filePath, out _);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void Validate_NormalSourceDirectory_IsAllowed()
    {
        var srcDir = Path.Combine(_tempRoot, "src", "MyProject");
        Directory.CreateDirectory(srcDir);

        var error = _policy.Validate(srcDir, out var normalized, isDirectory: true);
        Assert.Null(error);
        Assert.False(string.IsNullOrEmpty(normalized));
    }

    [Fact]
    public void Validate_DeniedSegments_AreCaseInsensitiveOnWindows()
    {
        // Windows path comparisons are case-insensitive.
        // 'BIN' and 'bin' should both be denied.
        var binUpper = Path.Combine(_tempRoot, "BIN");
        Directory.CreateDirectory(binUpper);

        var error = _policy.Validate(binUpper, out _, isDirectory: true);

        // On Windows, Path.GetFullPath normalises the casing so the check works regardless.
        // On case-sensitive systems (Linux CI) the test is still valid because we're checking
        // the explicit directory path – just that the segment check itself is case-insensitive.
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    // ── Task 4: FILE_NOT_FOUND vs DIRECTORY_NOT_FOUND ─────────────────────────

    [Fact]
    public void Validate_MissingFile_ReturnsFileNotFound()
    {
        var missing = Path.Combine(_tempRoot, "does_not_exist.txt");
        // isDirectory = false (default) → FILE_NOT_FOUND
        var error = _policy.Validate(missing, out _);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.FileNotFound, error.Code);
    }

    [Fact]
    public void Validate_MissingDirectory_ReturnsDirectoryNotFound()
    {
        var missing = Path.Combine(_tempRoot, "does_not_exist_dir");
        // isDirectory = true → DIRECTORY_NOT_FOUND
        var error = _policy.Validate(missing, out _, isDirectory: true);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.DirectoryNotFound, error.Code);
    }

    [Fact]
    public void Validate_ExistingFile_ReturnsNullError()
    {
        var file = Path.Combine(_tempRoot, "hello.txt");
        File.WriteAllText(file, "hello");

        var error = _policy.Validate(file, out var normalized);
        Assert.Null(error);
        Assert.False(string.IsNullOrEmpty(normalized));
    }

    [Fact]
    public void Validate_ExistingDirectory_ReturnsNullError()
    {
        var dir = Path.Combine(_tempRoot, "subdir");
        Directory.CreateDirectory(dir);

        var error = _policy.Validate(dir, out var normalized, isDirectory: true);
        Assert.Null(error);
        Assert.False(string.IsNullOrEmpty(normalized));
    }
}
