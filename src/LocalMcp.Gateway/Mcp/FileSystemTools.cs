using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Gateway.Commands;

namespace LocalMcp.Gateway.Mcp;

[McpServerToolType]
public sealed class FileSystemTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly ILogger<FileSystemTools> _logger;

    public FileSystemTools(ICommandDispatcher dispatcher, ILogger<FileSystemTools> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    [McpServerTool(Name = "fs_read", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Reads the content of a file on a target Windows agent device.")]
    public async Task<CallToolResult> ReadFileAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the file to read")] string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        var command = new ReadFileCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path
        };

        return await DispatchAsync<ReadFileResult>(command, "fs_read", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_tree", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Returns a bounded directory tree for a path inside an allowed root.")]
    public async Task<CallToolResult> GetTreeAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the root directory of the tree")] string path,
        [Description("The maximum depth of the tree (default: 4, hard limit: 10)")] int maxDepth = 4,
        [Description("The maximum number of entries to return (default: 1000, hard limit: 5000)")] int maxEntries = 1000,
        [Description("Whether to include hidden files/folders (default: false)")] bool includeHidden = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (maxDepth < 1 || maxDepth > 10)
            return CreateErrorResult("INVALID_REQUEST", "maxDepth must be between 1 and 10.");

        if (maxEntries < 1 || maxEntries > 5000)
            return CreateErrorResult("INVALID_REQUEST", "maxEntries must be between 1 and 5000.");

        var command = new TreeCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            MaxDepth = maxDepth,
            MaxEntries = maxEntries,
            IncludeHidden = includeHidden
        };

        return await DispatchAsync<TreeResult>(command, "fs_tree", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lists the immediate contents of a directory on a target Windows agent device.")]
    public async Task<CallToolResult> ListDirectoryAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the directory to list")] string path,
        [Description("Whether to include hidden files/folders (default: false)")] bool includeHidden = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        var command = new ListDirectoryCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            IncludeHidden = includeHidden
        };

        return await DispatchAsync<ListDirectoryResult>(command, "fs_list", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_search", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Recursively searches for files matching a pattern or query in a directory on a target Windows agent device.")]
    public async Task<CallToolResult> SearchFilesAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the directory to search in")] string path,
        [Description("The search query (filename or content search)")] string query,
        [Description("The search mode: 'name' or 'content'")] string mode,
        [Description("Optional glob pattern to filter files (e.g. *.cs)")] string? filePattern = null,
        [Description("Whether the search is case sensitive (default: false)")] bool caseSensitive = false,
        [Description("Maximum results to return (default: 100, hard limit: 500)")] int maxResults = 100,
        [Description("Maximum size in bytes of files to search in content mode (default: 1MB)")] long maxFileBytes = 1048576,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (string.IsNullOrEmpty(query))
            return CreateErrorResult("SEARCH_QUERY_REQUIRED", "Search query is required.");

        if (string.IsNullOrWhiteSpace(mode) ||
            (!string.Equals(mode, "name", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(mode, "content", StringComparison.OrdinalIgnoreCase)))
        {
            return CreateErrorResult("INVALID_SEARCH_MODE", "Invalid search mode. Supported modes are 'name' or 'content'.");
        }

        if (maxResults < 1 || maxResults > 500)
            return CreateErrorResult("INVALID_REQUEST", "maxResults must be between 1 and 500.");

        var command = new SearchFilesCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Query = query,
            Mode = mode,
            FilePattern = filePattern,
            CaseSensitive = caseSensitive,
            MaxResults = maxResults,
            MaxFileBytes = maxFileBytes
        };

        return await DispatchAsync<SearchFilesResult>(command, "fs_search", deviceId, cancellationToken);
    }

    // ──────────────────────────────────────────────
    // Private transport helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Dispatches a command to the agent, awaits the result, and returns a <see cref="CallToolResult"/>.
    /// Centralises: dispatch, timeout propagation, structured error conversion, success mapping, and logging.
    /// Each tool method remains responsible for input validation and command construction.
    /// </summary>
    private async Task<CallToolResult> DispatchAsync<TResult>(
        AgentCommand command,
        string toolName,
        string deviceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _dispatcher.SendAsync<TResult>(command, cancellationToken);

            if (result.Success && result.Data != null)
            {
                var contentJson = JsonSerializer.Serialize(result.Data, JsonOptions.Default);
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = contentJson }],
                    IsError = false
                };
            }

            var errorCode = result.Error?.Code ?? "INTERNAL_ERROR";
            var errorMessage = result.Error?.Message ?? "An unexpected error occurred during command execution.";
            return CreateErrorResult(errorCode, errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing {ToolName} for device {DeviceId}", toolName, deviceId);
            return CreateErrorResult("INTERNAL_ERROR", "An unexpected error occurred on the gateway.");
        }
    }

    private static CallToolResult CreateErrorResult(string code, string message)
    {
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
            IsError = true
        };
    }
}
