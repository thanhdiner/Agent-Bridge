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

public sealed class WriteToolsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileAccessOptions _options;

    public WriteToolsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_WriteToolsTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);

        _options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            WritableRoots = new List<string> { _tempRoot },
            DeniedSegments = new List<string> { ".git", "bin", "obj" },
            DeniedFileNames = new List<string> { "secret.txt" },
            DeniedWriteFileNames = new List<string> { ".env", ".env.*", "id_rsa", "id_ed25519" },
            DeniedWriteExtensions = new List<string> { ".pem", ".key", ".pfx", ".p12" },
            MaxReadBytes = 2097152,
            MaxWriteBytes = 500
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                // Reset read-only files before deletion
                foreach (var f in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(_tempRoot, true);
            }
        }
        catch { }
    }

    private static string Sha256Hex(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private FileSystemExecutor MakeExecutor() =>
        new FileSystemExecutor(
            new PathPolicy(Options.Create(_options)),
            Options.Create(_options),
            NullLogger<FileSystemExecutor>.Instance);

    // ────────────────────────────────────────────────────────────────
    // PathPolicy – write authorization
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void PathPolicy_EmptyWritableRoots_ReturnsNotConfigured()
    {
        var opts = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            WritableRoots = new List<string>()
        };
        var policy = new PathPolicy(Options.Create(opts));
        var target = Path.Combine(_tempRoot, "test.txt");

        var error = policy.AuthorizeWriteFile(target, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.WritableRootNotConfigured, error.Code);
    }

    [Fact]
    public void PathPolicy_WriteOutsideWritableRoot_ReturnsWriteNotAllowed()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");

        var error = policy.AuthorizeWriteFile(outside, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.WriteNotAllowed, error.Code);
    }

    [Fact]
    public void PathPolicy_PrefixCollision_WritableRootNotEscapedByPrefix()
    {
        // e.g. tempRoot = C:\foo, sibling = C:\foo-evil should be rejected
        var siblingDir = _tempRoot + "-sibling";
        Directory.CreateDirectory(siblingDir);
        try
        {
            var policy = new PathPolicy(Options.Create(_options));
            var target = Path.Combine(siblingDir, "escape.txt");
            var error = policy.AuthorizeWriteFile(target, out _);
            Assert.NotNull(error);
            Assert.Equal(ErrorCodes.WriteNotAllowed, error.Code);
        }
        finally
        {
            Directory.Delete(siblingDir, true);
        }
    }

    [Fact]
    public void PathPolicy_CaseInsensitivePath_IsAccepted()
    {
        // Windows is case-insensitive; mixed case of allowed root should still work
        var policy = new PathPolicy(Options.Create(_options));
        var upperRoot = _tempRoot.ToUpperInvariant();
        var target = Path.Combine(upperRoot, "upper_case.txt");

        var error = policy.AuthorizeWriteFile(target, out _);

        // Should be allowed (null = no error)
        Assert.Null(error);
    }

    [Fact]
    public void PathPolicy_TraversalEscape_ReturnsWriteNotAllowed()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var traversal = Path.Combine(_tempRoot, "..", "escape.txt");

        var error = policy.AuthorizeWriteFile(traversal, out _);

        Assert.NotNull(error);
        // Either WriteNotAllowed or PathOutsideAllowedRoot
        Assert.True(
            error.Code == ErrorCodes.WriteNotAllowed ||
            error.Code == ErrorCodes.PathOutsideAllowedRoot);
    }

    [Fact]
    public void PathPolicy_DeniedSegment_ReturnsAccessDenied()
    {
        var policy = new PathPolicy(Options.Create(_options));
        // Create the .git directory so the path resolves but then hits the segment deny list
        var gitDir = Path.Combine(_tempRoot, ".git");
        Directory.CreateDirectory(gitDir);
        try
        {
            var gitPath = Path.Combine(gitDir, "config");
            // Create the file so the path is physically valid
            File.WriteAllText(gitPath, "");
            var error = policy.AuthorizeWriteFile(gitPath, out _);
            Assert.NotNull(error);
            Assert.Equal(ErrorCodes.AccessDenied, error.Code);
        }
        finally
        {
            Directory.Delete(gitDir, true);
        }
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("id_rsa")]
    [InlineData("id_ed25519")]
    public void PathPolicy_DeniedExactFileName_ReturnsAccessDenied(string filename)
    {
        var policy = new PathPolicy(Options.Create(_options));
        var target = Path.Combine(_tempRoot, filename);

        var error = policy.AuthorizeWriteFile(target, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Theory]
    [InlineData(".env.local")]
    [InlineData(".env.production")]
    [InlineData(".env.staging")]
    public void PathPolicy_DeniedWildcardFileName_ReturnsAccessDenied(string filename)
    {
        var policy = new PathPolicy(Options.Create(_options));
        var target = Path.Combine(_tempRoot, filename);

        var error = policy.AuthorizeWriteFile(target, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Theory]
    [InlineData("cert.pem")]
    [InlineData("server.key")]
    [InlineData("client.pfx")]
    [InlineData("store.p12")]
    public void PathPolicy_DeniedWriteExtension_ReturnsAccessDenied(string filename)
    {
        var policy = new PathPolicy(Options.Create(_options));
        var target = Path.Combine(_tempRoot, filename);

        var error = policy.AuthorizeWriteFile(target, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void PathPolicy_ReadOnlyFile_ReturnsFileReadOnly()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var target = Path.Combine(_tempRoot, "readonly.txt");
        File.WriteAllText(target, "content");
        File.SetAttributes(target, FileAttributes.ReadOnly);

        try
        {
            var error = policy.AuthorizeWriteFile(target, out _);
            Assert.NotNull(error);
            Assert.Equal(ErrorCodes.FileReadOnly, error.Code);
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    [Fact]
    public void PathPolicy_DirectoryAsTarget_ReturnsAccessDenied()
    {
        var policy = new PathPolicy(Options.Create(_options));
        // Provide an existing directory path as the file target
        var error = policy.AuthorizeWriteFile(_tempRoot, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Fact]
    public void PathPolicy_MissingParentDirectory_ReturnsDirectoryNotFound()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var target = Path.Combine(_tempRoot, "nonexistent_dir", "file.txt");

        var error = policy.AuthorizeWriteFile(target, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.DirectoryNotFound, error.Code);
    }

    // ────────────────────────────────────────────────────────────────
    // Executor – write correctness
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Executor_WriteNewFile_CreatesFileWithoutBOM()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "newfile.txt");

        var result = await executor.WriteFileAsync(target, "Hello world", null, true, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Created);
        Assert.Equal(11, result.Data.BytesWritten);
        var raw = File.ReadAllBytes(target);
        // Must not start with UTF-8 BOM (EF BB BF)
        Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF);
        Assert.Equal("Hello world", File.ReadAllText(target));
    }

    [Fact]
    public async Task Executor_WriteExistingFile_HashRequired()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "exist.txt");
        File.WriteAllText(target, "original");

        var result = await executor.WriteFileAsync(target, "new content", null, false, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ExpectedHashRequired, result.Error?.Code);
    }

    [Fact]
    public async Task Executor_WriteExistingFile_StaleHashConflict()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "conflict.txt");
        File.WriteAllText(target, "original");

        var result = await executor.WriteFileAsync(target, "new content", "wrong-hash-stale", false, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FileConflict, result.Error?.Code);
    }

    [Fact]
    public async Task Executor_WriteOversizedContent_ReturnsTooLarge()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "large.txt");
        var largeContent = new string('A', 1000); // MaxWriteBytes is 500

        var result = await executor.WriteFileAsync(target, largeContent, null, true, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FileTooLarge, result.Error?.Code);
    }

    [Fact]
    public async Task Executor_WriteFile_ConcurrentNewFile_ReturnsAlreadyExists()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "race.txt");

        // Write one result; then attempt write without hash — simulates race
        var r1 = await executor.WriteFileAsync(target, "first", null, true, Guid.NewGuid(), CancellationToken.None);
        Assert.True(r1.Success);

        // Second attempt without expectedSha256 on now-existing file = EXPECTED_HASH_REQUIRED
        var r2 = await executor.WriteFileAsync(target, "second", null, true, Guid.NewGuid(), CancellationToken.None);
        Assert.False(r2.Success);
        Assert.Equal(ErrorCodes.ExpectedHashRequired, r2.Error?.Code);
    }

    [Fact]
    public async Task Executor_ErrorMessages_DoNotContainInternalPaths()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "info_leak_test.txt");
        File.WriteAllText(target, "data");

        // Trigger a conflict
        var result = await executor.WriteFileAsync(target, "new", "bad-hash", false, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        // Error message must not contain temp paths or exception details
        var msg = result.Error.Message;
        Assert.DoesNotContain(".tmp_", msg);
        Assert.DoesNotContain("Exception", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────
    // Executor – patch correctness
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Executor_Patch_Success()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "patchme.txt");
        var original = "Line 1\nLine 2\nLine 3";
        File.WriteAllText(target, original, new UTF8Encoding(false));
        var sha = Sha256Hex(original);

        var edits = new List<PatchEdit>
        {
            new PatchEdit { OldText = "Line 2", NewText = "Line Two" },
            new PatchEdit { OldText = "Line 3", NewText = "Line Three" }
        };

        var result = await executor.PatchFileAsync(target, sha, edits, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.EditsApplied);
        Assert.Equal("Line 1\nLine Two\nLine Three", File.ReadAllText(target));
    }

    [Fact]
    public async Task Executor_Patch_OversizedResult_ReturnsTooLarge()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "patchlarge.txt");
        var original = "small";
        File.WriteAllText(target, original, new UTF8Encoding(false));
        var sha = Sha256Hex(original);

        var edits = new List<PatchEdit>
        {
            new PatchEdit { OldText = "small", NewText = new string('X', 600) } // exceeds MaxWriteBytes=500
        };

        var result = await executor.PatchFileAsync(target, sha, edits, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FileTooLarge, result.Error?.Code);
    }

    [Fact]
    public async Task Executor_Patch_OverlapRejected()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "overlap.txt");
        var original = "ABCDEF";
        File.WriteAllText(target, original, new UTF8Encoding(false));
        var sha = Sha256Hex(original);

        var edits = new List<PatchEdit>
        {
            new PatchEdit { OldText = "BCD", NewText = "123" },
            new PatchEdit { OldText = "CDE", NewText = "456" }
        };

        var result = await executor.PatchFileAsync(target, sha, edits, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PatchEditsOverlap, result.Error?.Code);
    }

    [Fact]
    public async Task Executor_Patch_AmbiguousRejected()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "ambiguous.txt");
        var original = "HELLO HELLO";
        File.WriteAllText(target, original, new UTF8Encoding(false));
        var sha = Sha256Hex(original);

        var edits = new List<PatchEdit>
        {
            new PatchEdit { OldText = "HELLO", NewText = "HI", ReplaceAll = false }
        };

        var result = await executor.PatchFileAsync(target, sha, edits, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PatchTargetAmbiguous, result.Error?.Code);
    }

    [Fact]
    public async Task Executor_Patch_ErrorMessages_DoNotLeakInternals()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "patch_leak.txt");
        var original = "data";
        File.WriteAllText(target, original, new UTF8Encoding(false));

        var edits = new List<PatchEdit>
        {
            new PatchEdit { OldText = "data", NewText = "ok" }
        };

        // Pass wrong hash to trigger conflict
        var result = await executor.PatchFileAsync(target, "bad-hash", edits, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        var msg = result.Error?.Message ?? "";
        Assert.DoesNotContain(".tmp_", msg);
        Assert.DoesNotContain(_tempRoot, msg);
        Assert.DoesNotContain("Exception", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────
    // Executor – UTF-8 encoding
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Executor_ReadFile_ValidUtf8WithoutBOM_Succeeds()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "utf8_no_bom.txt");
        File.WriteAllBytes(target, Encoding.UTF8.GetBytes("Hello UTF-8"));

        var result = await executor.ReadFileAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("utf-8", result.Data!.Encoding);
        Assert.Equal("Hello UTF-8", result.Data.Content);
    }

    [Fact]
    public async Task Executor_ReadFile_ValidUtf8WithBOM_Succeeds()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "utf8_bom.txt");
        File.WriteAllBytes(target, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("BOM content")).ToArray());

        var result = await executor.ReadFileAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("utf-8-bom", result.Data!.Encoding);
        Assert.Equal("BOM content", result.Data.Content);
    }

    [Fact]
    public async Task Executor_ReadFile_InvalidUtf8_ReturnsUnsupportedEncoding()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "invalid_utf8.txt");
        // 0x80 is an invalid lone continuation byte in UTF-8
        File.WriteAllBytes(target, new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x80, 0x21 });

        var result = await executor.ReadFileAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.UnsupportedTextEncoding, result.Error?.Code);
    }

    [Fact]
    public async Task Executor_ReadFile_BinaryFile_ReturnsBinaryNotSupported()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "binary.bin");
        File.WriteAllBytes(target, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });

        var result = await executor.ReadFileAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.BinaryFileNotSupported, result.Error?.Code);
    }

    [Fact]
    public async Task Executor_WriteNewFile_HasNoBOM()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "written_no_bom.txt");

        var result = await executor.WriteFileAsync(target, "test content", null, true, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        var raw = File.ReadAllBytes(target);
        Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF,
            "Written file must not have UTF-8 BOM");
    }

    // ────────────────────────────────────────────────────────────────
    // Executor – temp-file cleanup
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Executor_Write_NoOrphanedTempFiles_AfterSuccess()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "clean_write.txt");

        await executor.WriteFileAsync(target, "data", null, true, Guid.NewGuid(), CancellationToken.None);

        var tempFiles = Directory.GetFiles(_tempRoot, ".tmp_*");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task Executor_Patch_NoOrphanedTempFiles_AfterSuccess()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "clean_patch.txt");
        var content = "patch me";
        File.WriteAllText(target, content, new UTF8Encoding(false));
        var sha = Sha256Hex(content);

        var edits = new List<PatchEdit> { new PatchEdit { OldText = "patch me", NewText = "done" } };
        await executor.PatchFileAsync(target, sha, edits, Guid.NewGuid(), CancellationToken.None);

        var tempFiles = Directory.GetFiles(_tempRoot, ".tmp_*");
        Assert.Empty(tempFiles);
    }

    // ────────────────────────────────────────────────────────────────
    // Executor – cancellation
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Executor_WriteFile_AlreadyCancelled_ReturnsCancelled()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "cancel_write.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await executor.WriteFileAsync(target, "data", null, true, Guid.NewGuid(), cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CommandCancelled, result.Error?.Code);
    }

    [Fact]
    public async Task Executor_PatchFile_AlreadyCancelled_ReturnsCancelled()
    {
        var executor = MakeExecutor();
        var target = Path.Combine(_tempRoot, "cancel_patch.txt");
        var content = "patch me";
        File.WriteAllText(target, content, new UTF8Encoding(false));
        var sha = Sha256Hex(content);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var edits = new List<PatchEdit> { new PatchEdit { OldText = "patch me", NewText = "done" } };
        var result = await executor.PatchFileAsync(target, sha, edits, Guid.NewGuid(), cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CommandCancelled, result.Error?.Code);
    }
}
