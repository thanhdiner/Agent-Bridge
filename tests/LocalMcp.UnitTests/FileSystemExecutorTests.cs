using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.BuildingBlocks.Errors;

namespace LocalMcp.UnitTests;

public sealed class FileSystemExecutorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemExecutor _executor;

    public FileSystemExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LocalMcp_FileSystemExecutorTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        var options = new LocalMcp.Agent.Windows.Security.FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempDir },
            DeniedSegments = new List<string> { ".git", ".ssh", "node_modules" },
            DeniedFileNames = new List<string> { ".env", "credentials.json" },
            MaxReadBytes = 10 * 1024 * 1024
        };
        var policy = new LocalMcp.Agent.Windows.Security.PathPolicy(Microsoft.Extensions.Options.Options.Create(options));
        _executor = new FileSystemExecutor(policy, Microsoft.Extensions.Options.Options.Create(options), NullLogger<FileSystemExecutor>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    [Fact]
    public async Task ReadFileAsync_ValidUtf8File_ReturnsContentAndMetadata()
    {
        var filePath = Path.Combine(_tempDir, "test.txt");
        var text = "Hello, this is a test UTF-8 file.";
        await File.WriteAllTextAsync(filePath, text, new UTF8Encoding(false));

        var result = await _executor.ReadFileAsync(filePath, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("utf-8", result.Data.Encoding);
        Assert.Equal(text, result.Data.Content);
        Assert.Equal(filePath, result.Data.Path);
        Assert.Equal(new FileInfo(filePath).Length, result.Data.Size);
        Assert.NotEmpty(result.Data.Sha256);
    }

    [Fact]
    public async Task ReadFileAsync_Utf8FileWithBom_ReturnsContentWithoutBomAndCorrectEncoding()
    {
        var filePath = Path.Combine(_tempDir, "test_bom.txt");
        var text = "BOM File Content";
        var encodingWithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllTextAsync(filePath, text, encodingWithBom);

        var result = await _executor.ReadFileAsync(filePath, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("utf-8-bom", result.Data.Encoding);
        Assert.Equal(text, result.Data.Content);
    }

    [Fact]
    public async Task ReadFileAsync_EmptyTextFile_ReturnsEmptyContent()
    {
        var filePath = Path.Combine(_tempDir, "empty.txt");
        await File.WriteAllTextAsync(filePath, string.Empty);

        var result = await _executor.ReadFileAsync(filePath, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(string.Empty, result.Data.Content);
        Assert.Equal(0, result.Data.Size);
    }

    [Fact]
    public async Task ReadFileAsync_BinaryFile_ReturnsBinaryFileNotSupported()
    {
        var filePath = Path.Combine(_tempDir, "binary.dat");
        var data = new byte[] { 0x01, 0x02, 0x00, 0x04, 0x05 };
        await File.WriteAllBytesAsync(filePath, data);

        var result = await _executor.ReadFileAsync(filePath, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCodes.BinaryFileNotSupported, result.Error.Code);
    }

    [Fact]
    public async Task ReadFileAsync_CancelledToken_ReturnsCancelledError()
    {
        var filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "cancel me");

        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _executor.ReadFileAsync(filePath, Guid.NewGuid(), cts.Token);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCodes.CommandCancelled, result.Error.Code);
    }

    #region fs_tree Tests

    [Fact]
    public async Task GetTreeAsync_ValidDirectory_ReturnsTreeStructure()
    {
        var subDir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subDir);
        var filePath = Path.Combine(subDir, "file.txt");
        await File.WriteAllTextAsync(filePath, "test content");

        var result = await _executor.GetTreeAsync(_tempDir, maxDepth: 4, maxEntries: 100, includeHidden: false, commandId: Guid.NewGuid(), cancellationToken: CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(result.Data.Truncated);
        Assert.Equal(2, result.Data.Entries.Count);
        Assert.Contains(result.Data.Entries, e => e.Name == "sub" && e.Type == "directory" && e.Depth == 1);
        Assert.Contains(result.Data.Entries, e => e.Name == "file.txt" && e.Type == "file" && e.Depth == 2 && e.RelativePath == $"sub{Path.DirectorySeparatorChar}file.txt");
    }

    [Fact]
    public async Task GetTreeAsync_MaxDepthLimit_DoesNotRecurseFurther()
    {
        var depth1 = Path.Combine(_tempDir, "d1");
        var depth2 = Path.Combine(depth1, "d2");
        Directory.CreateDirectory(depth2);
        var fileInD2 = Path.Combine(depth2, "file.txt");
        await File.WriteAllTextAsync(fileInD2, "hello");

        // maxDepth = 1, should only list d1, not d2 or file.txt
        var result = await _executor.GetTreeAsync(_tempDir, maxDepth: 1, maxEntries: 100, includeHidden: false, commandId: Guid.NewGuid(), cancellationToken: CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data.Entries);
        Assert.Equal("d1", result.Data.Entries[0].Name);
    }

    [Fact]
    public async Task GetTreeAsync_MaxEntriesLimit_TruncatesResult()
    {
        for (int i = 0; i < 5; i++)
        {
            var filePath = Path.Combine(_tempDir, $"file_{i}.txt");
            await File.WriteAllTextAsync(filePath, "test");
        }

        var result = await _executor.GetTreeAsync(_tempDir, maxDepth: 4, maxEntries: 3, includeHidden: false, commandId: Guid.NewGuid(), cancellationToken: CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Truncated);
        Assert.Equal(3, result.Data.Entries.Count);
    }

    [Fact]
    public async Task GetTreeAsync_DeniedDirectory_SkipsIt()
    {
        var allowedSub = Path.Combine(_tempDir, "allowed");
        var deniedSub = Path.Combine(_tempDir, "node_modules");
        Directory.CreateDirectory(allowedSub);
        Directory.CreateDirectory(deniedSub);

        var fileInAllowed = Path.Combine(allowedSub, "ok.txt");
        var fileInDenied = Path.Combine(deniedSub, "blocked.txt");
        await File.WriteAllTextAsync(fileInAllowed, "ok");
        await File.WriteAllTextAsync(fileInDenied, "blocked");

        var result = await _executor.GetTreeAsync(_tempDir, maxDepth: 4, maxEntries: 100, includeHidden: false, commandId: Guid.NewGuid(), cancellationToken: CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains(result.Data.Entries, e => e.Name == "allowed");
        Assert.DoesNotContain(result.Data.Entries, e => e.Name == "node_modules");
        Assert.DoesNotContain(result.Data.Entries, e => e.Name == "blocked.txt");
    }

    [Fact]
    public async Task GetTreeAsync_ReparsePoint_IsSkipped()
    {
        var targetFile = Path.Combine(Path.GetTempPath(), "tree_reparse_target_" + Guid.NewGuid() + ".txt");
        await File.WriteAllTextAsync(targetFile, "secret target");
        var linkPath = Path.Combine(_tempDir, "reparse_link.txt");

        try
        {
            File.CreateSymbolicLink(linkPath, targetFile);
        }
        catch (Exception)
        {
            // Skip test if environment lacks permission to create symlinks
            return;
        }

        try
        {
            var result = await _executor.GetTreeAsync(_tempDir, maxDepth: 4, maxEntries: 100, includeHidden: false, commandId: Guid.NewGuid(), cancellationToken: CancellationToken.None);
            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.DoesNotContain(result.Data.Entries, e => e.Name == "reparse_link.txt");
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
    public async Task GetTreeAsync_Cancellation_ReturnsCancelledError()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _executor.GetTreeAsync(_tempDir, maxDepth: 4, maxEntries: 100, includeHidden: false, commandId: Guid.NewGuid(), cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CommandCancelled, result.Error?.Code);
    }

    #endregion

    #region fs_list Tests

    [Fact]
    public async Task ListDirectoryAsync_ListsImmediateChildrenSorted()
    {
        var subDir = Path.Combine(_tempDir, "sub_dir");
        Directory.CreateDirectory(subDir);
        var subSub = Path.Combine(subDir, "nested");
        Directory.CreateDirectory(subSub);

        var fileA = Path.Combine(_tempDir, "a_file.txt");
        var fileB = Path.Combine(_tempDir, "b_file.txt");
        await File.WriteAllTextAsync(fileA, "A");
        await File.WriteAllTextAsync(fileB, "B");

        var result = await _executor.ListDirectoryAsync(_tempDir, maxEntries: 1000, commandId: Guid.NewGuid(), cancellationToken: CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(1, result.Data.TotalDirectories);
        Assert.Equal(2, result.Data.TotalFiles);

        // Directories first, then files sorted alphabetically
        Assert.Equal("sub_dir", result.Data.Directories[0].Name);
        Assert.Equal("a_file.txt", result.Data.Files[0].Name);
        Assert.Equal("b_file.txt", result.Data.Files[1].Name);
    }

    [Fact]
    public async Task ListDirectoryAsync_MissingDirectory_ReturnsDirectoryNotFound()
    {
        var missingPath = Path.Combine(_tempDir, "missing_folder");
        var result = await _executor.ListDirectoryAsync(missingPath, maxEntries: 1000, commandId: Guid.NewGuid(), cancellationToken: CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.DirectoryNotFound, result.Error?.Code);
    }

    #endregion

    #region fs_search Tests

    [Fact]
    public async Task SearchFilesAsync_NameMode_FindsMatchingFilename()
    {
        var matchedFile = Path.Combine(_tempDir, "find_me_file.txt");
        var otherFile = Path.Combine(_tempDir, "other.txt");
        await File.WriteAllTextAsync(matchedFile, "content");
        await File.WriteAllTextAsync(otherFile, "content");

        var result = await _executor.SearchFilesAsync(_tempDir, query: "find_me", maxResults: 100, maxDepth: 4, commandId: Guid.NewGuid(), cancellationToken: CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data.Matches);
        Assert.Equal("find_me_file.txt", Path.GetFileName(result.Data.Matches[0].FullPath));
        Assert.Equal("name", result.Data.Matches[0].MatchType);
    }

    [Fact]
    public async Task SearchFilesAsync_ContentMode_FindsText()
    {
        var file = Path.Combine(_tempDir, "search_content.txt");
        await File.WriteAllTextAsync(file, "Line 1\nTarget keyword here\nLine 3");

        var result = await _executor.SearchFilesAsync(_tempDir, query: "Target keyword", maxResults: 100, maxDepth: 4, commandId: Guid.NewGuid(), cancellationToken: CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data.Matches);
        Assert.Equal(2, result.Data.Matches[0].LineNumber);
        Assert.Equal("Target keyword here", result.Data.Matches[0].LinePreview);
        Assert.Equal("content", result.Data.Matches[0].MatchType);
    }

    [Fact]
    public async Task SearchFilesAsync_CaseInsensitive_FindsText()
    {
        var file = Path.Combine(_tempDir, "case.txt");
        await File.WriteAllTextAsync(file, "keyword Keyword KEYWORD");

        var result = await _executor.SearchFilesAsync(_tempDir, query: "Keyword", maxResults: 100, maxDepth: 4, commandId: Guid.NewGuid(), cancellationToken: CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data.Matches); // Matches the single line containing all keywords
    }

    [Fact]
    public async Task SearchFilesAsync_OversizedFile_IsSkipped()
    {
        var file = Path.Combine(_tempDir, "large_search.txt");
        using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(1100000);
            fs.Seek(1099000, SeekOrigin.Begin);
            var bytes = Encoding.UTF8.GetBytes("keyword");
            fs.Write(bytes, 0, bytes.Length);
        }

        var result = await _executor.SearchFilesAsync(_tempDir, query: "keyword", maxResults: 100, maxDepth: 4, commandId: Guid.NewGuid(), cancellationToken: CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.Matches);
    }

    #endregion
}
