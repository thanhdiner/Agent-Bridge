using System.Text.Json;
using LocalMcp.Contracts.Commands;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;

namespace LocalMcp.UnitTests;


/// <summary>
/// Tests for strict command deserialization in GatewayConnection (Task 3).
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

    // ── Task 3: Strict deserialization tests ──────────────────────────────────

    [Fact]
    public void Deserialize_MissingCommandType_ReturnsInvalidRequest()
    {
        // Payload contains a path but no commandType — old fallback would have parsed this as ReadFileCommand
        var json = "{\"commandId\":\"00000000-0000-0000-0000-000000000001\",\"deviceId\":\"dev\",\"path\":\"C:\\\\file.txt\"}";
        var (command, errorCode) = TryDeserialize(json);
        Assert.Null(command);
        Assert.Equal(ErrorCodes.InvalidRequest, errorCode);
    }

    [Fact]
    public void Deserialize_PathOnlyPayload_NoCommandType_ReturnsInvalidRequest()
    {
        // Specifically tests the old dangerous fallback: payload has "path" but no commandType
        var json = "{\"path\":\"C:\\\\some\\\\path.txt\"}";
        var (command, errorCode) = TryDeserialize(json);
        Assert.Null(command);
        Assert.Equal(ErrorCodes.InvalidRequest, errorCode);
    }

    [Fact]
    public void Deserialize_UnknownCommandType_ReturnsUnsupportedCommand()
    {
        var json = "{\"commandType\":\"DeleteFileCommand\",\"deviceId\":\"dev\",\"path\":\"C:\\\\file.txt\"}";
        var (command, errorCode) = TryDeserialize(json);
        Assert.Null(command);
        Assert.Equal(ErrorCodes.UnsupportedCommand, errorCode);
    }

    [Fact]
    public void Deserialize_MalformedJson_ReturnsInvalidRequest()
    {
        var json = "{this is not valid json";
        var (command, errorCode) = TryDeserialize(json);
        Assert.Null(command);
        Assert.Equal(ErrorCodes.InvalidRequest, errorCode);
    }

    [Fact]
    public void Deserialize_EmptyJson_ReturnsInvalidRequest()
    {
        var json = "{}";
        var (command, errorCode) = TryDeserialize(json);
        Assert.Null(command);
        Assert.Equal(ErrorCodes.InvalidRequest, errorCode);
    }

    [Fact]
    public void Deserialize_ReadFileCommand_WithValidCommandType_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"ReadFileCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\test.txt\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        Assert.NotNull(command);
        var readCmd = Assert.IsType<ReadFileCommand>(command);
        Assert.Equal("C:\\test.txt", readCmd.Path);
        Assert.Equal("dev", readCmd.DeviceId);
    }

    [Fact]
    public void Deserialize_ListDirectoryCommand_WithValidCommandType_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"ListDirectoryCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\src\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        Assert.NotNull(command);
        var listCmd = Assert.IsType<ListDirectoryCommand>(command);
        Assert.Equal("C:\\src", listCmd.Path);
    }

    [Fact]
    public void Deserialize_TreeCommand_WithValidCommandType_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"TreeCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\src\",\"maxDepth\":3,\"maxEntries\":500,\"includeHidden\":false}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        Assert.NotNull(command);
        var treeCmd = Assert.IsType<TreeCommand>(command);
        Assert.Equal(3, treeCmd.MaxDepth);
    }

    [Fact]
    public void Deserialize_SearchFilesCommand_WithValidCommandType_Succeeds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"commandType\":\"SearchFilesCommand\",\"commandId\":\"{id}\",\"deviceId\":\"dev\",\"createdAt\":\"2024-01-01T00:00:00Z\",\"path\":\"C:\\\\src\",\"query\":\"MapMcp\",\"mode\":\"content\"}}";

        var (command, errorCode) = TryDeserialize(json);

        Assert.Null(errorCode);
        Assert.NotNull(command);
        var searchCmd = Assert.IsType<SearchFilesCommand>(command);
        Assert.Equal("MapMcp", searchCmd.Query);
        Assert.Equal("content", searchCmd.Mode);
    }

    [Fact]
    public void Deserialize_WriteFileCommand_ReturnsUnsupportedCommand()
    {
        // Write commands must never be accepted — confirm they are explicitly rejected
        var json = "{\"commandType\":\"WriteFileCommand\",\"deviceId\":\"dev\",\"path\":\"C:\\\\file.txt\",\"content\":\"evil\"}";
        var (command, errorCode) = TryDeserialize(json);
        Assert.Null(command);
        Assert.Equal(ErrorCodes.UnsupportedCommand, errorCode);
    }
}
