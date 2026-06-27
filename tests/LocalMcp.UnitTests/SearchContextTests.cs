using System.Security.Cryptography;
using System.Text;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalMcp.UnitTests;

public sealed class SearchContextTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly FileSystemExecutor _executor;

    public SearchContextTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "LocalMcp_SearchContextTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDirectory);

        var options = new FileAccessOptions
        {
            AllowedRoots = [_tempDirectory],
            WritableRoots = [_tempDirectory],
            DeniedSegments = [".git", ".ssh", "node_modules"],
            DeniedFileNames = [".env", "credentials.json"],
            MaxReadBytes = 10 * 1024 * 1024,
            MaxWriteBytes = 10 * 1024 * 1024
        };
        var policy = new PathPolicy(Options.Create(options));
        _executor = new FileSystemExecutor(
            policy,
            Options.Create(options),
            NullLogger<FileSystemExecutor>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task SearchContextAsync_LiteralMatch_ReturnsContextAndSha256()
    {
        var path = Path.Combine(_tempDirectory, "sample.cs");
        await File.WriteAllTextAsync(
            path,
            "line one\nline before\nTarget value\nline after\nline five",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var result = await SearchAsync(
            query: "target",
            contextBefore: 1,
            contextAfter: 1);

        Assert.True(result.Success);
        var match = Assert.Single(result.Data!.Matches);
        Assert.Equal("sample.cs", match.RelativePath);
        Assert.Equal(3, match.LineNumber);
        Assert.Equal("Target", match.MatchedText);
        Assert.Equal("Target value", match.LineText);
        Assert.Equal(new[] { "line before" }, match.BeforeLines);
        Assert.Equal(new[] { "line after" }, match.AfterLines);

        var expectedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
        Assert.Equal(expectedHash, match.Sha256);
    }

    [Fact]
    public async Task SearchContextAsync_RegexAndCaseSensitive_ReturnsOnlyExactCase()
    {
        var path = Path.Combine(_tempDirectory, "regex.cs");
        await File.WriteAllTextAsync(path, "TODO fix this\ntodo ignore this\nTODO: second");

        var result = await SearchAsync(
            query: "^TODO\\b",
            useRegex: true,
            caseSensitive: true,
            contextBefore: 0,
            contextAfter: 0);

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Matches.Count);
        Assert.All(result.Data.Matches, match => Assert.Equal("TODO", match.MatchedText));
    }

    [Fact]
    public async Task SearchContextAsync_IncludeAndExcludeGlobs_FilterFilesAndDirectories()
    {
        var sourceDirectory = Path.Combine(_tempDirectory, "src");
        var objectDirectory = Path.Combine(_tempDirectory, "obj");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(objectDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "included.cs"), "needle");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "ignored.txt"), "needle");
        await File.WriteAllTextAsync(Path.Combine(objectDirectory, "generated.cs"), "needle");

        var result = await SearchAsync(
            query: "needle",
            includeGlobs: ["**/*.cs"],
            excludeGlobs: ["**/obj/**"]);

        Assert.True(result.Success);
        var match = Assert.Single(result.Data!.Matches);
        Assert.Equal("src/included.cs", match.RelativePath);
    }

    [Fact]
    public async Task SearchContextAsync_InvalidRegex_ReturnsInvalidRequest()
    {
        var result = await SearchAsync(query: "(", useRegex: true);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task SearchContextAsync_MaxResults_SetsTruncated()
    {
        var path = Path.Combine(_tempDirectory, "many.txt");
        await File.WriteAllTextAsync(path, "needle one\nneedle two\nneedle three");

        var result = await SearchAsync(query: "needle", maxResults: 1);

        Assert.True(result.Success);
        Assert.Single(result.Data!.Matches);
        Assert.True(result.Data.Truncated);
    }

    private Task<LocalMcp.Contracts.Results.CommandResult<LocalMcp.Contracts.Results.SearchContextResult>> SearchAsync(
        string query,
        bool useRegex = false,
        bool caseSensitive = false,
        IReadOnlyList<string>? includeGlobs = null,
        IReadOnlyList<string>? excludeGlobs = null,
        int contextBefore = 2,
        int contextAfter = 2,
        int maxResults = 100,
        int maxDepth = 4)
    {
        return _executor.SearchContextAsync(
            _tempDirectory,
            query,
            useRegex,
            caseSensitive,
            includeGlobs ?? [],
            excludeGlobs ?? [],
            contextBefore,
            contextAfter,
            maxResults,
            maxDepth,
            Guid.NewGuid(),
            CancellationToken.None);
    }
}
