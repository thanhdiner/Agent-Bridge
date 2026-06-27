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
                nameof(ListDirectoryCommand) => JsonSerializer.Deserialize<ListDirectoryCommand>(payload, JsonOptions.Default),
                nameof(SearchFilesCommand) => JsonSerializer.Deserialize<SearchFilesCommand>(payload, JsonOptions.Default),
                nameof(TreeCommand) => JsonSerializer.Deserialize<TreeCommand>(payload, JsonOptions.Default),
                nameof(WriteFileCommand) => JsonSerializer.Deserialize<WriteFileCommand>(payload, JsonOptions.Default),
                nameof(PatchFileCommand) => JsonSerializer.Deserialize<PatchFileCommand>(payload, JsonOptions.Default),
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
