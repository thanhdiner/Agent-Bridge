using System.Security.Cryptography;
using System.Text;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalMcp.UnitTests;

public sealed class CrossVolumeMoveTests : IDisposable
{
    private static readonly Encoding NoBomUtf8 = new UTF8Encoding(false);
    private readonly string _tempRoot;
    private readonly FileAccessOptions _options;

    public CrossVolumeMoveTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_CrossVolumeMoveTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);

        _options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            WritableRoots = new List<string> { _tempRoot },
            DeniedSegments = new List<string> { ".git", "bin", "obj" },
            DeniedFileNames = new List<string> { ".env", "id_rsa" },
            DeniedWriteFileNames = new List<string>(),
            DeniedWriteExtensions = new List<string>(),
            MaxReadBytes = 2_097_152,
            MaxWriteBytes = 524_288
        };
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_tempRoot))
                return;

            foreach (var file in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }

            Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private FileSystemExecutor MakeExecutor()
    {
        var policy = new PathPolicy(Options.Create(_options));
        return new FileSystemExecutor(
            policy,
            Options.Create(_options),
            NullLogger<FileSystemExecutor>.Instance);
    }

    private string TempFile(string name, string content)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, content, NoBomUtf8);
        return path;
    }

    private static string Sha256Hex(string content)
    {
        return Convert.ToHexString(SHA256.HashData(NoBomUtf8.GetBytes(content))).ToLowerInvariant();
    }

    private void AssertNoMoveArtifacts()
    {
        Assert.Empty(Directory.GetFiles(_tempRoot, "move-temp-*.tmp"));
        Assert.Empty(Directory.GetFiles(_tempRoot, "move-backup-*.tmp"));
    }

    [Fact]
    public async Task MoveAsync_SameVolume_UsesFastPathAndReturnsFingerprint()
    {
        const string content = "fast-path";
        var source = TempFile("fast-source.txt", content);
        var destination = Path.Combine(_tempRoot, "fast-destination.txt");
        var executor = MakeExecutor();

        var result = await executor.MoveAsync(
            source,
            destination,
            overwrite: false,
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(result.Data!.MovedAcrossVolume);
        Assert.False(result.Data.IsDirectory);
        Assert.Equal((long)NoBomUtf8.GetByteCount(content), result.Data.BytesMoved);
        Assert.Equal(Sha256Hex(content), result.Data.Sha256);
        Assert.False(File.Exists(source));
        Assert.Equal(content, File.ReadAllText(destination));
        AssertNoMoveArtifacts();
    }

    [Fact]
    public async Task MoveAsync_ForcedCrossVolumeFallback_CopiesVerifiesAndDeletesSource()
    {
        const string content = "cross-volume";
        var source = TempFile("fallback-source.txt", content);
        var destination = Path.Combine(_tempRoot, "fallback-destination.txt");
        var executor = MakeExecutor();
        executor.ShouldUseCrossVolumeMoveFallbackHook = (_, _) => true;

        var result = await executor.MoveAsync(
            source,
            destination,
            overwrite: false,
            expectedSha256: Sha256Hex(content),
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.MovedAcrossVolume);
        Assert.Equal((long)NoBomUtf8.GetByteCount(content), result.Data.BytesMoved);
        Assert.Equal(Sha256Hex(content), result.Data.Sha256);
        Assert.False(File.Exists(source));
        Assert.Equal(content, File.ReadAllText(destination));
        AssertNoMoveArtifacts();
    }

    [Fact]
    public async Task MoveAsync_ForcedFallbackWrongExpectedHash_LeavesSourceUntouched()
    {
        var source = TempFile("hash-source.txt", "actual");
        var destination = Path.Combine(_tempRoot, "hash-destination.txt");
        var executor = MakeExecutor();
        executor.ShouldUseCrossVolumeMoveFallbackHook = (_, _) => true;

        var result = await executor.MoveAsync(
            source,
            destination,
            overwrite: false,
            expectedSha256: "deadbeef",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ConcurrencyConflict, result.Error!.Code);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(destination));
        AssertNoMoveArtifacts();
    }

    [Fact]
    public async Task MoveAsync_ForcedFallbackOverwrite_ReplacesDestinationAndCleansBackup()
    {
        var source = TempFile("overwrite-source.txt", "new-content");
        var destination = TempFile("overwrite-destination.txt", "old-content");
        var executor = MakeExecutor();
        executor.ShouldUseCrossVolumeMoveFallbackHook = (_, _) => true;

        var result = await executor.MoveAsync(
            source,
            destination,
            overwrite: true,
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.MovedAcrossVolume);
        Assert.False(File.Exists(source));
        Assert.Equal("new-content", File.ReadAllText(destination));
        AssertNoMoveArtifacts();
    }

    [Fact]
    public async Task MoveAsync_CopyFailure_DoesNotDeleteSourceOrPublishDestination()
    {
        var source = TempFile("copy-failure-source.txt", "source");
        var destination = Path.Combine(_tempRoot, "copy-failure-destination.txt");
        var executor = MakeExecutor();
        executor.ShouldUseCrossVolumeMoveFallbackHook = (_, _) => true;
        executor.OnBeforeCrossVolumeCopyHook = _ => throw new IOException("Injected copy failure");

        var result = await executor.MoveAsync(
            source,
            destination,
            overwrite: false,
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WriteError, result.Error!.Code);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(destination));
        AssertNoMoveArtifacts();
    }

    [Fact]
    public async Task MoveAsync_SourceChangesAfterCopy_FailsVerificationWithoutPublishing()
    {
        var source = TempFile("verify-source.txt", "before");
        var destination = Path.Combine(_tempRoot, "verify-destination.txt");
        var executor = MakeExecutor();
        executor.ShouldUseCrossVolumeMoveFallbackHook = (_, _) => true;
        executor.OnAfterCrossVolumeCopyHook = path =>
        {
            File.WriteAllText(path, "after", NoBomUtf8);
            return Task.CompletedTask;
        };

        var result = await executor.MoveAsync(
            source,
            destination,
            overwrite: false,
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ConcurrencyConflict, result.Error!.Code);
        Assert.True(File.Exists(source));
        Assert.Equal("after", File.ReadAllText(source));
        Assert.False(File.Exists(destination));
        AssertNoMoveArtifacts();
    }

    [Fact]
    public async Task MoveAsync_DestinationAppearsBeforePublish_FailsWithoutDeletingSource()
    {
        var source = TempFile("concurrent-source.txt", "source-data");
        var destination = Path.Combine(_tempRoot, "concurrent-destination.txt");
        var executor = MakeExecutor();
        executor.ShouldUseCrossVolumeMoveFallbackHook = (_, _) => true;
        executor.OnBeforeCrossVolumePublishHook = path =>
        {
            File.WriteAllText(path, "appeared", NoBomUtf8);
            return Task.CompletedTask;
        };

        var result = await executor.MoveAsync(
            source,
            destination,
            overwrite: false,
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.AccessDenied, result.Error!.Code);
        Assert.True(File.Exists(source));
        Assert.Equal("appeared", File.ReadAllText(destination));
        AssertNoMoveArtifacts();
    }

    [Fact]
    public async Task MoveAsync_PublishedDestinationChanges_RollsBackBeforeDeletingSource()
    {
        var source = TempFile("destination-change-source.txt", "source-data");
        var destination = Path.Combine(_tempRoot, "destination-change-target.txt");
        var executor = MakeExecutor();
        executor.ShouldUseCrossVolumeMoveFallbackHook = (_, _) => true;
        executor.OnBeforeCrossVolumeSourceDeleteHook = _ =>
        {
            File.WriteAllText(destination, "tampered", NoBomUtf8);
            return Task.CompletedTask;
        };

        var result = await executor.MoveAsync(
            source,
            destination,
            false,
            null,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ConcurrencyConflict, result.Error!.Code);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(destination));
        Assert.Equal("true", result.Error.Details!["rollbackSucceeded"].Single());
        AssertNoMoveArtifacts();
    }

    [Fact]
    public async Task MoveAsync_SourceDeleteFailure_RollsBackNewDestination()
    {
        var source = TempFile("delete-failure-source.txt", "source-data");
        var destination = Path.Combine(_tempRoot, "delete-failure-destination.txt");
        var executor = MakeExecutor();
        executor.ShouldUseCrossVolumeMoveFallbackHook = (_, _) => true;
        executor.OnBeforeCrossVolumeSourceDeleteHook = _ => throw new IOException("Injected delete failure");

        var result = await executor.MoveAsync(
            source,
            destination,
            overwrite: false,
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WriteError, result.Error!.Code);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(destination));
        Assert.Equal("true", result.Error.Details!["rollbackSucceeded"].Single());
        Assert.Equal("false", result.Error.Details["destinationPublished"].Single());
        AssertNoMoveArtifacts();
    }

    [Fact]
    public async Task MoveAsync_SourceDeleteFailureDuringOverwrite_RestoresOldDestination()
    {
        var source = TempFile("rollback-source.txt", "new-data");
        var destination = TempFile("rollback-destination.txt", "old-data");
        var executor = MakeExecutor();
        executor.ShouldUseCrossVolumeMoveFallbackHook = (_, _) => true;
        executor.OnBeforeCrossVolumeSourceDeleteHook = _ => throw new IOException("Injected delete failure");

        var result = await executor.MoveAsync(
            source,
            destination,
            overwrite: true,
            expectedSha256: null,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WriteError, result.Error!.Code);
        Assert.True(File.Exists(source));
        Assert.Equal("old-data", File.ReadAllText(destination));
        Assert.Equal("true", result.Error.Details!["rollbackSucceeded"].Single());
        AssertNoMoveArtifacts();
    }

    [Fact]
    public async Task MoveAsync_CancellationAfterCopy_CleansTemporaryFile()
    {
        var source = TempFile("cancel-source.txt", "cancel-data");
        var destination = Path.Combine(_tempRoot, "cancel-destination.txt");
        var executor = MakeExecutor();
        using var cts = new CancellationTokenSource();
        executor.ShouldUseCrossVolumeMoveFallbackHook = (_, _) => true;
        executor.OnAfterCrossVolumeCopyHook = _ =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };

        var result = await executor.MoveAsync(
            source,
            destination,
            overwrite: false,
            expectedSha256: null,
            Guid.NewGuid(),
            cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CommandCancelled, result.Error!.Code);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(destination));
        AssertNoMoveArtifacts();
    }

    [Fact]
    public void IsCrossVolumeMoveError_RecognizesWindowsNotSameDeviceCode()
    {
        var crossVolume = new HResultIOException(unchecked((int)0x80070011));
        var ordinary = new HResultIOException(unchecked((int)0x80070020));

        Assert.True(FileSystemExecutor.IsCrossVolumeMoveError(crossVolume));
        Assert.False(FileSystemExecutor.IsCrossVolumeMoveError(ordinary));
    }

    private sealed class HResultIOException : IOException
    {
        public HResultIOException(int hResult)
        {
            HResult = hResult;
        }
    }
}
