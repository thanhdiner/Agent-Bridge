using System.Text.Json;
using LocalMcp.Contracts.Commands;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;

namespace LocalMcp.UnitTests;

/// <summary>
/// Tests for strict command deserialization in GatewayConnection.
/// These tests directly exercise the deserialization logic by parsing the same
/// JSON that would arrive via SignalR, using the same code paths as GatewayConnection.
/// </summary>
public sealed class CommandDeserializerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Simulates the strict deserialization logic extracted from GatewayConnection.On("ReceiveCommand").
    /// Returns (command, errorCode) where command is null when error is non-null.
    /// </summary>
    private static (AgentCommand? command, string? errorCode) TryDeserialize(string rawJson)
    {
        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(rawJson, JsonOptions.Default);
        }
        catch (JsonException)
        {
            return (null, ErrorCodes.InvalidRequest);
        }

        // Strict: commandType is required
        if (!json.TryGetProperty("commandType", out var typeProp))
        {
            return (null, ErrorCodes.InvalidRequest);
        }

        var typeName = typeProp.GetString();
        var payload = json.GetRawText();

        AgentCommand? command;
        try
        {
            command = typeName switch
            {
                nameof(ReadFileCommand) => JsonSerializer.Deserialize<ReadFileCommand>(payload, JsonOptions.Default),
                nameof(ReadRangeCommand) => JsonSerializer.Deserialize<ReadRangeCommand>(payload, JsonOptions.Default),
                nameof(ListDirectoryCommand) => JsonSerializer.Deserialize<ListDirectoryCommand>(payload, JsonOptions.Default),
                nameof(SearchFilesCommand) => JsonSerializer.Deserialize<SearchFilesCommand>(payload, JsonOptions.Default),
                nameof(SearchContextCommand) => JsonSerializer.Deserialize<SearchContextCommand>(payload, JsonOptions.Default),
                nameof(GitStatusCommand) => JsonSerializer.Deserialize<GitStatusCommand>(payload, JsonOptions.Default),
                nameof(GitDiffCommand) => JsonSerializer.Deserialize<GitDiffCommand>(payload, JsonOptions.Default),
                nameof(GitLogCommand) => JsonSerializer.Deserialize<GitLogCommand>(payload, JsonOptions.Default),
                nameof(GitShowCommand) => JsonSerializer.Deserialize<GitShowCommand>(payload, JsonOptions.Default),
                nameof(ProjectCheckCommand) => JsonSerializer.Deserialize<ProjectCheckCommand>(payload, JsonOptions.Default),
                nameof(TreeCommand) => JsonSerializer.Deserialize<TreeCommand>(payload, JsonOptions.Default),
                nameof(WriteFileCommand) => JsonSerializer.Deserialize<WriteFileCommand>(payload, JsonOptions.Default),
                nameof(PatchFileCommand) => JsonSerializer.Deserialize<PatchFileCommand>(payload, JsonOptions.Default),
                nameof(MultiFilePatchCommand) => JsonSerializer.Deserialize<MultiFilePatchCommand>(payload, JsonOptions.Default),
                nameof(CreateDirectoryCommand) => JsonSerializer.Deserialize<CreateDirectoryCommand>(payload, JsonOptions.Default),
                nameof(StatCommand) => JsonSerializer.Deserialize<StatCommand>(payload, JsonOptions.Default),
                nameof(BatchStatCommand) => JsonSerializer.Deserialize<BatchStatCommand>(payload, JsonOptions.Default),
                nameof(BatchReadCommand) => JsonSerializer.Deserialize<BatchReadCommand>(payload, JsonOptions.Default),
                nameof(MoveCommand) => JsonSerializer.Deserialize<MoveCommand>(payload, JsonOptions.Default),
                nameof(CopyCommand) => JsonSerializer.Deserialize<CopyCommand>(payload, JsonOptions.Default),
                nameof(DeleteCommand) => JsonSerializer.Deserialize<DeleteCommand>(payload, JsonOptions.Default),
                nameof(RemoveDirectoryCommand) => JsonSerializer.Deserialize<RemoveDirectoryCommand>(payload, JsonOptions.Default),
                _ => null
            };
        }
        catch (JsonException)
        {
            return (null, ErrorCodes.InvalidRequest);
        }

        if (command is null)
        {
            return (null, ErrorCodes.UnsupportedCommand);
        }

        return (command, null);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_ReadFileCommand_WithValidJson_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"ReadFileCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\file.txt\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        Assert.NotNull(command);
        var readCmd = Assert.IsType<ReadFileCommand>(command);
        Assert.Equal(id, readCmd.CommandId);
        Assert.Equal("dev", readCmd.DeviceId);
        Assert.Equal("C:\\file.txt", readCmd.Path);
    }

    [Fact]
    public void Deserialize_ReadRangeCommand_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"ReadRangeCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:/src/file.txt\",\"startLine\":25,\"lineCount\":50}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var rangeCommand = Assert.IsType<ReadRangeCommand>(command);
        Assert.Equal("C:/src/file.txt", rangeCommand.Path);
        Assert.Equal(25L, rangeCommand.StartLine);
        Assert.Equal(50, rangeCommand.LineCount);
    }

    [Fact]
    public void Deserialize_ReadRangeCommand_WithoutOptionalFields_HasDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"ReadRangeCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:/src/file.txt\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var rangeCommand = Assert.IsType<ReadRangeCommand>(command);
        Assert.Equal(1L, rangeCommand.StartLine);
        Assert.Equal(200, rangeCommand.LineCount);
    }

    [Fact]
    public void Deserialize_ListDirectoryCommand_WithValidJson_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"ListDirectoryCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\src\",\"maxEntries\":50}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        Assert.NotNull(command);
        var listCmd = Assert.IsType<ListDirectoryCommand>(command);
        Assert.Equal("C:\\src", listCmd.Path);
        Assert.Equal(50, listCmd.MaxEntries);
    }

    [Fact]
    public void Deserialize_SearchFilesCommand_WithValidCommandType_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"SearchFilesCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\src\",\"query\":\"MapMcp\",\"maxResults\":50,\"maxDepth\":3}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        Assert.NotNull(command);
        var searchCmd = Assert.IsType<SearchFilesCommand>(command);
        Assert.Equal("MapMcp", searchCmd.Query);
        Assert.Equal(50, searchCmd.MaxResults);
        Assert.Equal(3, searchCmd.MaxDepth);
    }

    [Fact]
    public void Deserialize_SearchContextCommand_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"SearchContextCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:/src\",\"query\":\"TODO.*fix\",\"useRegex\":true,\"caseSensitive\":true,\"includeGlobs\":[\"**/*.cs\"],\"excludeGlobs\":[\"**/obj/**\"],\"contextBefore\":3,\"contextAfter\":4,\"maxResults\":25,\"maxDepth\":6}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var searchCommand = Assert.IsType<SearchContextCommand>(command);
        Assert.Equal("TODO.*fix", searchCommand.Query);
        Assert.True(searchCommand.UseRegex);
        Assert.True(searchCommand.CaseSensitive);
        Assert.Equal(new[] { "**/*.cs" }, searchCommand.IncludeGlobs);
        Assert.Equal(new[] { "**/obj/**" }, searchCommand.ExcludeGlobs);
        Assert.Equal(3, searchCommand.ContextBefore);
        Assert.Equal(4, searchCommand.ContextAfter);
        Assert.Equal(25, searchCommand.MaxResults);
        Assert.Equal(6, searchCommand.MaxDepth);
    }

    [Fact]
    public void Deserialize_SearchContextCommand_WithoutOptionalFields_HasDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"SearchContextCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:/src\",\"query\":\"needle\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var searchCommand = Assert.IsType<SearchContextCommand>(command);
        Assert.False(searchCommand.UseRegex);
        Assert.False(searchCommand.CaseSensitive);
        Assert.Empty(searchCommand.IncludeGlobs);
        Assert.Empty(searchCommand.ExcludeGlobs);
        Assert.Equal(2, searchCommand.ContextBefore);
        Assert.Equal(2, searchCommand.ContextAfter);
        Assert.Equal(100, searchCommand.MaxResults);
        Assert.Equal(4, searchCommand.MaxDepth);
    }

    [Fact]
    public void Deserialize_GitStatusCommand_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"GitStatusCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:/src/repo\",\"includeUntracked\":false,\"maxEntries\":25}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var statusCommand = Assert.IsType<GitStatusCommand>(command);
        Assert.Equal("C:/src/repo", statusCommand.Path);
        Assert.False(statusCommand.IncludeUntracked);
        Assert.Equal(25, statusCommand.MaxEntries);
    }

    [Fact]
    public void Deserialize_GitStatusCommand_WithoutOptionalFields_HasDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"GitStatusCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:/src/repo\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var statusCommand = Assert.IsType<GitStatusCommand>(command);
        Assert.True(statusCommand.IncludeUntracked);
        Assert.Equal(1000, statusCommand.MaxEntries);
    }

    [Fact]
    public void Deserialize_GitDiffCommand_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"GitDiffCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:/src/repo\",\"staged\":true,\"includeUntracked\":false,\"pathSpecs\":[\"src/**/*.cs\"],\"contextLines\":7,\"maxBytes\":2048}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var diffCommand = Assert.IsType<GitDiffCommand>(command);
        Assert.Equal("C:/src/repo", diffCommand.Path);
        Assert.True(diffCommand.Staged);
        Assert.False(diffCommand.IncludeUntracked);
        Assert.Equal(new[] { "src/**/*.cs" }, diffCommand.PathSpecs);
        Assert.Equal(7, diffCommand.ContextLines);
        Assert.Equal(2048, diffCommand.MaxBytes);
    }

    [Fact]
    public void Deserialize_GitDiffCommand_WithoutOptionalFields_HasDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"GitDiffCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:/src/repo\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var diffCommand = Assert.IsType<GitDiffCommand>(command);
        Assert.False(diffCommand.Staged);
        Assert.True(diffCommand.IncludeUntracked);
        Assert.Empty(diffCommand.PathSpecs);
        Assert.Equal(3, diffCommand.ContextLines);
        Assert.Equal(1_048_576, diffCommand.MaxBytes);
    }

    [Fact]
    public void Deserialize_GitLogCommand_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"GitLogCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-27T00:00:00Z\",\"path\":\"C:/src/repo\",\"maxCount\":50,\"skip\":4,\"pathSpec\":\"src/App.cs\",\"author\":\"Ada\",\"since\":\"2026-01-01T00:00:00Z\",\"until\":\"2026-06-27T23:59:59Z\",\"includeStats\":true}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var logCommand = Assert.IsType<GitLogCommand>(command);
        Assert.Equal(50, logCommand.MaxCount);
        Assert.Equal(4, logCommand.Skip);
        Assert.Equal("src/App.cs", logCommand.PathSpec);
        Assert.Equal("Ada", logCommand.Author);
        Assert.True(logCommand.IncludeStats);
    }

    [Fact]
    public void Deserialize_GitLogCommand_WithoutOptionalFields_HasDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"GitLogCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-27T00:00:00Z\",\"path\":\"C:/src/repo\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var logCommand = Assert.IsType<GitLogCommand>(command);
        Assert.Equal(20, logCommand.MaxCount);
        Assert.Equal(0, logCommand.Skip);
        Assert.Null(logCommand.PathSpec);
        Assert.Null(logCommand.Author);
        Assert.False(logCommand.IncludeStats);
    }

    [Fact]
    public void Deserialize_GitShowCommand_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"GitShowCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-27T00:00:00Z\",\"path\":\"C:/src/repo\",\"revision\":\"HEAD~1\",\"pathSpecs\":[\"src/**/*.cs\"],\"includePatch\":false,\"includeStats\":false,\"contextLines\":9,\"maxBytes\":4096}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var showCommand = Assert.IsType<GitShowCommand>(command);
        Assert.Equal("HEAD~1", showCommand.Revision);
        Assert.Equal(new[] { "src/**/*.cs" }, showCommand.PathSpecs);
        Assert.False(showCommand.IncludePatch);
        Assert.False(showCommand.IncludeStats);
        Assert.Equal(9, showCommand.ContextLines);
        Assert.Equal(4096, showCommand.MaxBytes);
    }

    [Fact]
    public void Deserialize_GitShowCommand_WithoutOptionalFields_HasDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"GitShowCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2026-06-27T00:00:00Z\",\"path\":\"C:/src/repo\",\"revision\":\"HEAD\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var showCommand = Assert.IsType<GitShowCommand>(command);
        Assert.Empty(showCommand.PathSpecs);
        Assert.True(showCommand.IncludePatch);
        Assert.True(showCommand.IncludeStats);
        Assert.Equal(3, showCommand.ContextLines);
        Assert.Equal(1_048_576, showCommand.MaxBytes);
    }

    [Fact]
    public void Deserialize_ProjectCheckCommand_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"ProjectCheckCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:/src/repo\",\"projectType\":\"rust\",\"steps\":[\"build\",\"test\"],\"configuration\":\"Release\",\"timeoutSeconds\":120,\"maxOutputBytes\":4096}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var checkCommand = Assert.IsType<ProjectCheckCommand>(command);
        Assert.Equal("C:/src/repo", checkCommand.Path);
        Assert.Equal("rust", checkCommand.ProjectType);
        Assert.Equal(new[] { "build", "test" }, checkCommand.Steps);
        Assert.Equal("Release", checkCommand.Configuration);
        Assert.Equal(120, checkCommand.TimeoutSeconds);
        Assert.Equal(4096, checkCommand.MaxOutputBytes);
    }

    [Fact]
    public void Deserialize_ProjectCheckCommand_WithoutOptionalFields_HasDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"ProjectCheckCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:/src/repo\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var checkCommand = Assert.IsType<ProjectCheckCommand>(command);
        Assert.Equal("auto", checkCommand.ProjectType);
        Assert.Equal(new[] { "build", "test" }, checkCommand.Steps);
        Assert.Equal("Debug", checkCommand.Configuration);
        Assert.Equal(300, checkCommand.TimeoutSeconds);
        Assert.Equal(1_048_576, checkCommand.MaxOutputBytes);
    }

    [Fact]
    public void Deserialize_WriteFileCommand_WithValidJson_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"WriteFileCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\file.txt\",\"content\":\"hello\",\"createIfMissing\":true}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        Assert.NotNull(command);
        var writeCmd = Assert.IsType<WriteFileCommand>(command);
        Assert.Equal("hello", writeCmd.Content);
        Assert.True(writeCmd.CreateIfMissing);
    }

    [Fact]
    public void Deserialize_PatchFileCommand_WithValidJson_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"PatchFileCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\file.txt\",\"expectedSha256\":\"hash\",\"edits\":[{{\"oldText\":\"foo\",\"newText\":\"bar\",\"replaceAll\":true}}]}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        Assert.NotNull(command);
        var patchCmd = Assert.IsType<PatchFileCommand>(command);
        Assert.Equal("hash", patchCmd.ExpectedSha256);
        Assert.Single(patchCmd.Edits);
        Assert.Equal("foo", patchCmd.Edits[0].OldText);
        Assert.Equal("bar", patchCmd.Edits[0].NewText);
        Assert.True(patchCmd.Edits[0].ReplaceAll);
    }

    [Fact]
    public void Deserialize_CreateDirectoryCommand_WithValidJson_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"CreateDirectoryCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\new_dir\",\"recursive\":true}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        Assert.NotNull(command);
        var mkdirCmd = Assert.IsType<CreateDirectoryCommand>(command);
        Assert.Equal("C:\\new_dir", mkdirCmd.Path);
        Assert.True(mkdirCmd.Recursive);
    }

    [Fact]
    public void Deserialize_StatCommand_WithValidJson_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"StatCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\file.txt\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        Assert.NotNull(command);
        var statCmd = Assert.IsType<StatCommand>(command);
        Assert.Equal("C:\\file.txt", statCmd.Path);
    }

    [Fact]
    public void Deserialize_BatchStatCommand_WithPaths_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"BatchStatCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"paths\":[\"C:/src/a.txt\",\"C:/src/b\"]}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var batchCommand = Assert.IsType<BatchStatCommand>(command);
        Assert.Equal(new[] { "C:/src/a.txt", "C:/src/b" }, batchCommand.Paths);
    }

    [Fact]
    public void Deserialize_BatchReadCommand_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"BatchReadCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"paths\":[\"C:/src/a.txt\",\"C:/src/b.txt\"],\"maxBytesPerFile\":1024,\"maxTotalBytes\":4096}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var batchCommand = Assert.IsType<BatchReadCommand>(command);
        Assert.Equal(new[] { "C:/src/a.txt", "C:/src/b.txt" }, batchCommand.Paths);
        Assert.Equal(1024, batchCommand.MaxBytesPerFile);
        Assert.Equal(4096L, batchCommand.MaxTotalBytes);
    }

    [Fact]
    public void Deserialize_BatchReadCommand_WithoutOptionalFields_HasDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"BatchReadCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"paths\":[\"C:/src/a.txt\"]}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var batchCommand = Assert.IsType<BatchReadCommand>(command);
        Assert.Equal(262_144, batchCommand.MaxBytesPerFile);
        Assert.Equal(2_097_152L, batchCommand.MaxTotalBytes);
    }

    [Fact]
    public void Deserialize_MultiFilePatchCommand_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"MultiFilePatchCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"items\":[{{\"path\":\"C:/src/a.txt\",\"expectedSha256\":\"abc\",\"edits\":[{{\"oldText\":\"old\",\"newText\":\"new\",\"replaceAll\":false}}]}}]}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var batchCommand = Assert.IsType<MultiFilePatchCommand>(command);
        var item = Assert.Single(batchCommand.Items);
        Assert.Equal("C:/src/a.txt", item.Path);
        Assert.Equal("abc", item.ExpectedSha256);
        Assert.Single(item.Edits);
    }

    [Fact]
    public void Deserialize_MoveCommand_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"MoveCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\src\\\\a.txt\",\"destination\":\"C:\\\\dst\\\\b.txt\",\"overwrite\":true,\"expectedSha256\":\"abc123\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        Assert.NotNull(command);
        var moveCmd = Assert.IsType<MoveCommand>(command);
        Assert.Equal("C:\\src\\a.txt", moveCmd.Path);
        Assert.Equal("C:\\dst\\b.txt", moveCmd.Destination);
        Assert.True(moveCmd.Overwrite);
        Assert.Equal("abc123", moveCmd.ExpectedSha256);
    }

    [Fact]
    public void Deserialize_MoveCommand_WithoutOptionalFields_HasDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"MoveCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\src\\\\a.txt\",\"destination\":\"C:\\\\dst\\\\b.txt\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var moveCmd = Assert.IsType<MoveCommand>(command);
        Assert.False(moveCmd.Overwrite);
        Assert.Null(moveCmd.ExpectedSha256);
    }

    [Fact]
    public void Deserialize_CopyCommand_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"CopyCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\src\\\\a.txt\",\"destination\":\"C:\\\\dst\\\\a.txt\",\"overwrite\":true,\"expectedSourceSha256\":\"deadbeef\",\"recursive\":true,\"maxEntries\":250,\"maxTotalBytes\":2048}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        Assert.NotNull(command);
        var copyCmd = Assert.IsType<CopyCommand>(command);
        Assert.Equal("C:\\src\\a.txt", copyCmd.Path);
        Assert.Equal("C:\\dst\\a.txt", copyCmd.Destination);
        Assert.True(copyCmd.Overwrite);
        Assert.Equal("deadbeef", copyCmd.ExpectedSourceSha256);
        Assert.True(copyCmd.Recursive);
        Assert.Equal(250, copyCmd.MaxEntries);
        Assert.Equal(2048L, copyCmd.MaxTotalBytes);
    }

    [Fact]
    public void Deserialize_CopyCommand_WithoutOptionalFields_HasDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"CopyCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\src\\\\a.txt\",\"destination\":\"C:\\\\dst\\\\a.txt\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var copyCmd = Assert.IsType<CopyCommand>(command);
        Assert.False(copyCmd.Overwrite);
        Assert.Null(copyCmd.ExpectedSourceSha256);
        Assert.False(copyCmd.Recursive);
        Assert.Equal(1000, copyCmd.MaxEntries);
        Assert.Equal(104857600L, copyCmd.MaxTotalBytes);
    }

    [Fact]
    public void Deserialize_DeleteCommand_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"DeleteCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:/src/a.txt\",\"expectedSha256\":\"abc123\",\"missingOk\":true}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var deleteCmd = Assert.IsType<DeleteCommand>(command);
        Assert.Equal("C:/src/a.txt", deleteCmd.Path);
        Assert.Equal("abc123", deleteCmd.ExpectedSha256);
        Assert.True(deleteCmd.MissingOk);
    }

    [Fact]
    public void Deserialize_DeleteCommand_WithoutOptionalFields_HasDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"DeleteCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\src\\\\a.txt\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var deleteCmd = Assert.IsType<DeleteCommand>(command);
        Assert.Null(deleteCmd.ExpectedSha256);
        Assert.False(deleteCmd.MissingOk);
    }

    [Fact]
    public void Deserialize_RemoveDirectoryCommand_WithAllFields_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"RemoveDirectoryCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:/src/empty\",\"missingOk\":true}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var removeCommand = Assert.IsType<RemoveDirectoryCommand>(command);
        Assert.Equal("C:/src/empty", removeCommand.Path);
        Assert.True(removeCommand.MissingOk);
    }

    [Fact]
    public void Deserialize_RemoveDirectoryCommand_WithoutOptionalFields_HasDefaults()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"RemoveDirectoryCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:/src/empty\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        var removeCommand = Assert.IsType<RemoveDirectoryCommand>(command);
        Assert.False(removeCommand.MissingOk);
    }

    [Fact]
    public void Deserialize_UnknownCommandType_ReturnsUnsupportedCommand()
    {
        var json = "{\"commandType\":\"EvilCommand\",\"deviceId\":\"dev\"}";
        var (command, errorCode) = TryDeserialize(json);
        Assert.Null(command);
        Assert.Equal(ErrorCodes.UnsupportedCommand, errorCode);
    }

    [Fact]
    public void Deserialize_MissingCommandType_ReturnsInvalidRequest()
    {
        var json = "{\"deviceId\":\"dev\"}";
        var (command, errorCode) = TryDeserialize(json);
        Assert.Null(command);
        Assert.Equal(ErrorCodes.InvalidRequest, errorCode);
    }

    [Fact]
    public void Deserialize_MalformedJson_ReturnsInvalidRequest()
    {
        var json = "{invalid_json}";
        var (command, errorCode) = TryDeserialize(json);
        Assert.Null(command);
        Assert.Equal(ErrorCodes.InvalidRequest, errorCode);
    }
}
