using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.BuildingBlocks.Errors;

namespace LocalMcp.UnitTests;

[Collection("Sequential")]
public sealed class GitExecutorTests : IDisposable
{
    private readonly string _tempDir;

    public GitExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LocalMcp_GitTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        InitializeGitRepo();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                // Force delete read-only files that Git might have created
                foreach (var file in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore clean up errors in tests
        }
    }

    private void InitializeGitRepo()
    {
        RunGitCmd("init");
        RunGitCmd("config user.name \"Test User\"");
        RunGitCmd("config user.email \"test@example.com\"");
        RunGitCmd("config commit.gpgsign false");
    }

    private void RunGitCmd(string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = _tempDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo);
        process?.WaitForExit();
    }

    private (FileSystemExecutor Executor, PathPolicy Policy) CreateExecutor(long maxWriteBytes = 1048576)
    {
        var options = new FileAccessOptions
        {
            AllowedRoots = [_tempDir],
            WritableRoots = [_tempDir],
            MaxWriteBytes = maxWriteBytes
        };
        var policy = new PathPolicy(Options.Create(options));
        var executor = new FileSystemExecutor(policy, Options.Create(options), NullLogger<FileSystemExecutor>.Instance);
        return (executor, policy);
    }

    [Fact]
    public async Task GitRestoreFile_ModifiedFile_RestoresHEADVersion()
    {
        var (executor, _) = CreateExecutor();
        var filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "HEAD version\n");

        RunGitCmd("add test.txt");
        RunGitCmd("commit -m \"Initial\"");

        // Modify file
        await File.WriteAllTextAsync(filePath, "Modified version\n");

        var result = await executor.GitRestoreFileAsync(
            _tempDir,
            "test.txt",
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("HEAD", result.Data.Source);
        Assert.True(result.Data.Changed);
        
        var currentContent = await File.ReadAllTextAsync(filePath);
        Assert.Equal("HEAD version\n", currentContent);
    }

    [Fact]
    public async Task GitRestoreFile_DeletedFile_RestoresHEADVersion()
    {
        var (executor, _) = CreateExecutor();
        var filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "HEAD version\n");

        RunGitCmd("add test.txt");
        RunGitCmd("commit -m \"Initial\"");

        // Delete file
        File.Delete(filePath);

        var result = await executor.GitRestoreFileAsync(
            _tempDir,
            "test.txt",
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Null(result.Data.PreviousSha256);
        Assert.True(result.Data.Changed);

        var currentContent = await File.ReadAllTextAsync(filePath);
        Assert.Equal("HEAD version\n", currentContent);
    }

    [Fact]
    public async Task GitRestoreFile_ExpectedHashMismatch_ReturnsHashMismatch()
    {
        var (executor, _) = CreateExecutor();
        var filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "HEAD version\n");

        RunGitCmd("add test.txt");
        RunGitCmd("commit -m \"Initial\"");

        // Modify file
        await File.WriteAllTextAsync(filePath, "Modified version\n");

        var result = await executor.GitRestoreFileAsync(
            _tempDir,
            "test.txt",
            expectedSha256: new string('f', 64), // Mismatch expected hash
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.HashMismatch, result.Error?.Code);
    }

    [Fact]
    public async Task GitRestoreFile_UntrackedFile_ReturnsFileNotFound()
    {
        var (executor, _) = CreateExecutor();
        
        var result = await executor.GitRestoreFileAsync(
            _tempDir,
            "untracked.txt",
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FileNotFound, result.Error?.Code);
    }

    [Fact]
    public async Task GitRestoreFile_TraversalOrInvalidPaths_ReturnsInvalidRequestOrWriteNotAllowed()
    {
        var (executor, _) = CreateExecutor();

        var result1 = await executor.GitRestoreFileAsync(
            _tempDir,
            "../test.txt",
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.False(result1.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result1.Error?.Code);

        var result2 = await executor.GitRestoreFileAsync(
            _tempDir,
            "C:\\absolute\\path",
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.False(result2.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result2.Error?.Code);

        var result3 = await executor.GitRestoreFileAsync(
            _tempDir,
            "test.txt*",
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.False(result3.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result3.Error?.Code);
    }

    [Fact]
    public async Task GitRestoreFile_ExceedsMaxWriteBytes_ReturnsFileTooLarge()
    {
        var (executor, _) = CreateExecutor(maxWriteBytes: 5); // very small max bytes
        var filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "Too long content\n");

        RunGitCmd("add test.txt");
        RunGitCmd("commit -m \"Initial\"");

        var result = await executor.GitRestoreFileAsync(
            _tempDir,
            "test.txt",
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FileTooLarge, result.Error?.Code);
    }

    [Fact]
    public async Task GitRefreshIndex_CleanFile_Idempotent()
    {
        var (executor, _) = CreateExecutor();
        var filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "Clean content\n");

        RunGitCmd("add test.txt");
        RunGitCmd("commit -m \"Initial\"");

        var result = await executor.GitRefreshIndexAsync(
            _tempDir,
            "test.txt",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(result.Data.RewrittenFromIndex);
        Assert.True(result.Data.CleanAfterRefresh);
    }

    [Fact]
    public async Task GitRefreshIndex_ModifiedFile_ReturnsFileConflict()
    {
        var (executor, _) = CreateExecutor();
        var filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "Clean content\n");

        RunGitCmd("add test.txt");
        RunGitCmd("commit -m \"Initial\"");

        // Modify content (semantic change)
        await File.WriteAllTextAsync(filePath, "Different content\n");

        var result = await executor.GitRefreshIndexAsync(
            _tempDir,
            "test.txt",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FileConflict, result.Error?.Code);
    }

    [Fact]
    public async Task GitRefreshIndex_StatCacheOrCrlfMismatch_SucceedsRefresh()
    {
        var (executor, _) = CreateExecutor();
        var filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "Clean content\n");

        RunGitCmd("add test.txt");
        RunGitCmd("commit -m \"Initial\"");

        // Update modification time without changing content to make stat cache dirty
        File.SetLastWriteTime(filePath, DateTime.Now.AddDays(1));

        var result = await executor.GitRefreshIndexAsync(
            _tempDir,
            "test.txt",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.CleanAfterRefresh);
    }
}
