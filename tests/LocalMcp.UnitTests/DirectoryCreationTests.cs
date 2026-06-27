using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
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

public sealed class DirectoryCreationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileAccessOptions _options;

    public DirectoryCreationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_DirCreationTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);

        _options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            WritableRoots = new List<string> { _tempRoot },
            DeniedSegments = new List<string> { ".git", "bin" },
            DeniedFileNames = new List<string> { "secret_dir" },
            DeniedWriteFileNames = new List<string> { ".env", ".env.*" },
            DeniedWriteExtensions = new List<string> { ".pem" },
            MaxReadBytes = 1000,
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
    public void PathPolicy_CreateDir_AllowedAndWritableRoot_Succeeds()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var rawPath = Path.Combine(_tempRoot, "new_sub_dir");

        var error = policy.AuthorizeCreateDirectory(rawPath, out var normalizedPath, recursive: false);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(rawPath), normalizedPath);
    }

    [Fact]
    public void PathPolicy_CreateDir_EmptyWritableRoots_ReturnsWritableRootNotConfigured()
    {
        _options.WritableRoots = new List<string>();
        var policy = new PathPolicy(Options.Create(_options));
        var rawPath = Path.Combine(_tempRoot, "new_sub_dir");

        var error = policy.AuthorizeCreateDirectory(rawPath, out _, recursive: false);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.WritableRootNotConfigured, error.Code);
    }

    [Fact]
    public void PathPolicy_CreateDir_FileAlreadyExistsAtTarget_ReturnsAccessDenied()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var rawPath = Path.Combine(_tempRoot, "existing_file.txt");
        File.WriteAllText(rawPath, "hello");

        var error = policy.AuthorizeCreateDirectory(rawPath, out _, recursive: false);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void PathPolicy_CreateDir_RecursiveFalse_ParentDoesNotExist_ReturnsDirectoryNotFound()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var rawPath = Path.Combine(_tempRoot, "non_existent_parent", "target_dir");

        var error = policy.AuthorizeCreateDirectory(rawPath, out _, recursive: false);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.DirectoryNotFound, error.Code);
    }

    [Fact]
    public void PathPolicy_CreateDir_RecursiveTrue_ParentDoesNotExist_AllowsCreation()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var rawPath = Path.Combine(_tempRoot, "non_existent_parent", "target_dir");

        var error = policy.AuthorizeCreateDirectory(rawPath, out var normalizedPath, recursive: true);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(rawPath), normalizedPath);
    }

    [Fact]
    public void PathPolicy_CreateDir_DeniedSegment_ReturnsAccessDenied()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var rawPath = Path.Combine(_tempRoot, "bin", "sub_dir");

        var error = policy.AuthorizeCreateDirectory(rawPath, out _, recursive: true);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void PathPolicy_CreateDir_DeniedFilename_ReturnsAccessDenied()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var rawPath = Path.Combine(_tempRoot, "secret_dir");

        var error = policy.AuthorizeCreateDirectory(rawPath, out _, recursive: true);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void PathPolicy_CreateDir_DeniedWriteFilename_ReturnsAccessDenied()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var rawPath = Path.Combine(_tempRoot, ".env.production");

        var error = policy.AuthorizeCreateDirectory(rawPath, out _, recursive: true);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public async Task Executor_CreateDir_RecursiveFalse_Succeeds()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "new_sub_dir");

        var result = await executor.CreateDirectoryAsync(target, recursive: false, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Created);
        Assert.Equal(Path.GetFullPath(target), result.Data.Path);
        Assert.Single(result.Data.DirectoriesCreated);
        Assert.Equal(Path.GetFullPath(target), result.Data.DirectoriesCreated[0]);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task Executor_CreateDir_RecursiveTrue_CreatesMultipleDirectories()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var p1 = Path.Combine(_tempRoot, "parent");
        var target = Path.Combine(p1, "child");

        var result = await executor.CreateDirectoryAsync(target, recursive: true, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Created);
        Assert.Equal(2, result.Data.DirectoriesCreated.Count);
        Assert.Equal(Path.GetFullPath(p1), result.Data.DirectoriesCreated[0]);
        Assert.Equal(Path.GetFullPath(target), result.Data.DirectoriesCreated[1]);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task Executor_CreateDir_AlreadyExists_ReturnsCreatedFalse()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "existing_dir");
        Directory.CreateDirectory(target);

        var result = await executor.CreateDirectoryAsync(target, recursive: false, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(result.Data.Created);
        Assert.Empty(result.Data.DirectoriesCreated);
    }

    [Fact]
    public async Task Executor_CreateDir_DotEnvLocalChild_Fails()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, ".env.local", "child");

        var result = await executor.CreateDirectoryAsync(target, recursive: true, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.AccessDenied, result.Error?.Code);
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, ".env.local")));
    }

    [Fact]
    public async Task Executor_CreateDir_SafeDotEnvProductionChild_Fails()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "safe", ".env.production", "child");

        var result = await executor.CreateDirectoryAsync(target, recursive: true, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.AccessDenied, result.Error?.Code);
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "safe")));
    }

    [Fact]
    public async Task Executor_CreateDir_DeniedIntermediateSegment_Fails()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "bin", "child");

        var result = await executor.CreateDirectoryAsync(target, recursive: true, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.AccessDenied, result.Error?.Code);
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "bin")));
    }

    [Fact]
    public async Task Executor_CreateDir_SymlinkJunctionEscape_Fails()
    {
        var externalDir = Path.Combine(Path.GetTempPath(), "LocalMcp_ExternalTarget_" + Guid.NewGuid());
        Directory.CreateDirectory(externalDir);

        try
        {
            var linkPath = Path.Combine(_tempRoot, "junction_link");

            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /j \"{linkPath}\" \"{externalDir}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit();

            Assert.True(Directory.Exists(linkPath), "Directory junction was not created for test.");

            var policy = new PathPolicy(Options.Create(_options));
            var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
            var target = Path.Combine(linkPath, "child");

            var result = await executor.CreateDirectoryAsync(target, recursive: true, Guid.NewGuid(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Contains(result.Error!.Code, new[] { ErrorCodes.AccessDenied, ErrorCodes.WriteNotAllowed, ErrorCodes.PathOutsideAllowedRoot });
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            if (Directory.Exists(externalDir))
            {
                try { Directory.Delete(externalDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Executor_CreateDir_CancellationDuringRecursiveCreation_FailsAndRollsBack()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(_tempRoot, "will_be_cancelled", "child");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await executor.CreateDirectoryAsync(target, recursive: true, Guid.NewGuid(), cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CommandCancelled, result.Error?.Code);
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "will_be_cancelled")));
    }

    [Fact]
    public async Task Executor_CreateDir_RollbackAndPreExistingPreservation_Succeeds()
    {
        var preExisting = Path.Combine(_tempRoot, "pre_existing");
        Directory.CreateDirectory(preExisting);

        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);

        var target = Path.Combine(preExisting, "new_dir1", "secret_dir");

        var result = await executor.CreateDirectoryAsync(target, recursive: true, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.AccessDenied, result.Error?.Code);

        Assert.False(Directory.Exists(Path.Combine(preExisting, "new_dir1")));
        Assert.False(Directory.Exists(Path.Combine(preExisting, "new_dir1", "secret_dir")));

        Assert.True(Directory.Exists(preExisting));
    }

    [Fact]
    public async Task Executor_CreateDir_CancellationViaHook_FailsAndRollsBackOnlyCreatedSegment()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);

        var parentPath = Path.Combine(_tempRoot, "parent_to_cancel");
        var target = Path.Combine(parentPath, "child");

        using var cts = new CancellationTokenSource();

        executor.OnDirectorySegmentCreatedHook = (path) =>
        {
            if (string.Equals(path, parentPath, StringComparison.OrdinalIgnoreCase))
            {
                cts.Cancel();
            }
        };

        var result = await executor.CreateDirectoryAsync(target, recursive: true, Guid.NewGuid(), cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CommandCancelled, result.Error?.Code);
        Assert.False(Directory.Exists(parentPath));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Executor_CreateDir_OriginalPathIsJunction_Fails()
    {
        var targetDir = Path.Combine(Path.GetTempPath(), "LocalMcp_JuncTarget_" + Guid.NewGuid());
        Directory.CreateDirectory(targetDir);

        try
        {
            var linkPath = Path.Combine(_tempRoot, "direct_junction");
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /j \"{linkPath}\" \"{targetDir}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit();

            Assert.True(Directory.Exists(linkPath), "Junction was not created.");

            var policy = new PathPolicy(Options.Create(_options));
            var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
            var target = Path.Combine(linkPath, "child");

            var result = await executor.CreateDirectoryAsync(target, recursive: true, Guid.NewGuid(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.AccessDenied, result.Error?.Code);
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            if (Directory.Exists(targetDir))
            {
                try { Directory.Delete(targetDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Executor_CreateDir_ReplacedWithJunctionBeforeVerification_FailsAndRollsBack()
    {
        var targetDir = Path.Combine(Path.GetTempPath(), "LocalMcp_VerifyJuncTarget_" + Guid.NewGuid());
        Directory.CreateDirectory(targetDir);
        var sentinelFile = Path.Combine(targetDir, "sentinel.txt");
        File.WriteAllText(sentinelFile, "do not touch");

        var segmentPath = Path.Combine(_tempRoot, "verify_junc_segment");
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);
        var target = Path.Combine(segmentPath, "child");

        executor.OnDirectorySegmentCreatedHook = (path) =>
        {
            if (string.Equals(path, segmentPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(path);
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /j \"{path}\" \"{targetDir}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                proc?.WaitForExit();
            }
        };

        try
        {
            var result = await executor.CreateDirectoryAsync(target, recursive: true, Guid.NewGuid(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.AccessDenied, result.Error?.Code);

            // Rollback must not delete the targetDir or the sentinel file inside it
            Assert.True(Directory.Exists(targetDir));
            Assert.True(File.Exists(sentinelFile));
        }
        finally
        {
            if (Directory.Exists(targetDir))
            {
                try { Directory.Delete(targetDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Executor_CreateDir_ReplacedWithJunctionBeforeRollback_RollbackSkipsAndDoesNotTouchTarget()
    {
        var targetDir = Path.Combine(Path.GetTempPath(), "LocalMcp_RollbackJuncTarget_" + Guid.NewGuid());
        Directory.CreateDirectory(targetDir);
        var sentinelFile = Path.Combine(targetDir, "sentinel.txt");
        File.WriteAllText(sentinelFile, "do not touch");

        var segmentPath = Path.Combine(_tempRoot, "rollback_junc_segment");
        var target = Path.Combine(segmentPath, "secret_dir"); // secret_dir will fail verification, triggering rollback

        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(policy, Options.Create(_options), NullLogger<FileSystemExecutor>.Instance);

        executor.OnDirectorySegmentCreatedHook = (path) =>
        {
            if (string.Equals(path, segmentPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(path);
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /j \"{path}\" \"{targetDir}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                proc?.WaitForExit();
            }
        };

        try
        {
            var result = await executor.CreateDirectoryAsync(target, recursive: true, Guid.NewGuid(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.AccessDenied, result.Error?.Code);

            // Rollback must skip segmentPath because its attributes/physical destination changed
            // Ensure targetDir and the sentinel file are untouched
            Assert.True(Directory.Exists(targetDir));
            Assert.True(File.Exists(sentinelFile));
        }
        finally
        {
            if (Directory.Exists(targetDir))
            {
                try { Directory.Delete(targetDir, true); } catch { }
            }
        }
    }
}
