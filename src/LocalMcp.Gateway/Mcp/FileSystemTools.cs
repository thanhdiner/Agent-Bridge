using System.ComponentModel;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<FileSystemTools> _logger;

    public FileSystemTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<FileSystemTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(Name = "fs_read", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Reads the content of a file on a target Windows agent device. Requires files:read scope.")]
    public async Task<CallToolResult> ReadFileAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the file to read")] string path)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        }

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

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<ReadFileResult>(command, "fs_read", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_read_range", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Reads a bounded range of lines from a UTF-8 text file on a target Windows agent device without loading the whole file into memory. Requires files:read scope.")]
    public async Task<CallToolResult> ReadRangeAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the UTF-8 text file to read")] string path,
        [Description("The one-based line number at which to start reading (default: 1)")] long startLine = 1,
        [Description("The maximum number of lines to return (default: 200, hard limit: 1000)")] int lineCount = 200)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (startLine < 1)
            return CreateErrorResult("INVALID_REQUEST", "startLine must be greater than or equal to 1.");

        if (lineCount < 1 || lineCount > 1000)
            return CreateErrorResult("INVALID_REQUEST", "lineCount must be between 1 and 1000.");

        if (startLine > long.MaxValue - lineCount)
            return CreateErrorResult("INVALID_REQUEST", "The requested line range is too large.");

        var command = new ReadRangeCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            StartLine = startLine,
            LineCount = lineCount
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<ReadRangeResult>(command, "fs_read_range", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_tree", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Returns a bounded directory tree for a path inside an allowed root. Requires files:read scope.")]
    public async Task<CallToolResult> GetTreeAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the root directory of the tree")] string path,
        [Description("The maximum depth of the tree (default: 4, hard limit: 10)")] int maxDepth = 4,
        [Description("The maximum number of entries to return (default: 1000, hard limit: 5000)")] int maxEntries = 1000)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        }

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
            IncludeHidden = false
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<TreeResult>(command, "fs_tree", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lists the immediate contents of a directory on a target Windows agent device. Requires files:read scope.")]
    public async Task<CallToolResult> ListDirectoryAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the directory to list")] string path,
        [Description("The maximum number of entries to return (default: 1000, hard limit: 5000)")] int maxEntries = 1000)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        }

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (maxEntries < 1 || maxEntries > 5000)
            return CreateErrorResult("INVALID_REQUEST", "maxEntries must be between 1 and 5000.");

        var command = new ListDirectoryCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            MaxEntries = maxEntries
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<ListDirectoryResult>(command, "fs_list", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_search", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Recursively searches for files matching a pattern or query in a directory on a target Windows agent device. Requires files:read scope.")]
    public async Task<CallToolResult> SearchFilesAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the directory to search in")] string path,
        [Description("The search query (filename or content search)")] string query,
        [Description("Maximum results to return (default: 100, hard limit: 500)")] int maxResults = 100,
        [Description("Maximum depth to search (default: 4, hard limit: 10)")] int maxDepth = 4)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        }

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (string.IsNullOrEmpty(query))
            return CreateErrorResult("SEARCH_QUERY_REQUIRED", "Search query is required.");

        if (maxResults < 1 || maxResults > 500)
            return CreateErrorResult("INVALID_REQUEST", "maxResults must be between 1 and 500.");

        if (maxDepth < 1 || maxDepth > 10)
            return CreateErrorResult("INVALID_REQUEST", "maxDepth must be between 1 and 10.");

        var command = new SearchFilesCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Query = query,
            MaxResults = maxResults,
            MaxDepth = maxDepth
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<SearchFilesResult>(command, "fs_search", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_write", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Creates a new UTF-8 text file or replaces the complete content of an existing text file on a target Windows agent device. Requires files:write scope. Safe workflow: (1) Call fs_read first; (2) Inspect content; (3) Pass returned sha256 as expectedSha256; (4) Call fs_write. Re-read on conflict.")]
    public async Task<CallToolResult> WriteFileAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the file to write")] string path,
        [Description("The text content to write to the file")] string content,
        [Description("The expected SHA-256 hash of the existing file. Required if the file already exists.")] string? expectedSha256 = null,
        [Description("Whether to create the file if it does not exist (default: false)")] bool createIfMissing = false)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");
        }

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (content == null)
            return CreateErrorResult("INVALID_REQUEST", "content parameter is required.");

        var command = new WriteFileCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Content = content,
            ExpectedSha256 = expectedSha256,
            CreateIfMissing = createIfMissing
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<WriteFileResult>(command, "fs_write", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_patch", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Applies one or more exact text replacements to an existing UTF-8 text file on a target Windows agent device. Requires files:write scope. Safe workflow: (1) Call fs_read first; (2) Inspect content; (3) Pass returned sha256 as expectedSha256; (4) Call fs_patch. Re-read on conflict.")]
    public async Task<CallToolResult> PatchFileAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the file to patch")] string path,
        [Description("The expected SHA-256 hash of the current file content")] string expectedSha256,
        [Description("The list of exact text replacements to apply")] List<PatchEdit> edits)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");
        }

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (string.IsNullOrEmpty(expectedSha256))
            return CreateErrorResult("EXPECTED_HASH_REQUIRED", "expectedSha256 parameter is required.");

        if (edits == null || edits.Count == 0)
            return CreateErrorResult("PATCH_EDITS_REQUIRED", "edits parameter is required and cannot be empty.");

        var command = new PatchFileCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            ExpectedSha256 = expectedSha256,
            Edits = edits
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<PatchFileResult>(command, "fs_patch", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_mkdir", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Creates a directory or directories at the specified path on a target Windows agent device. Requires files:write scope. Recursive creation is supported.")]
    public async Task<CallToolResult> CreateDirectoryAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the directory to create")] string path,
        [Description("Whether to recursively create parent directories if missing (default: false)")] bool recursive = false)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");
        }

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        var command = new CreateDirectoryCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Recursive = recursive
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<CreateDirectoryResult>(command, "fs_mkdir", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_stat", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Gets file or directory status metadata (exists, size, type, sha256) on a target Windows agent device. Requires files:read scope.")]
    public async Task<CallToolResult> StatAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the file or directory to check")] string path)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        }

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        var command = new StatCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<StatResult>(command, "fs_stat", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_batch_stat", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Gets status metadata for between 1 and 100 file or directory paths on a target Windows agent device. Each path is evaluated independently and input order is preserved. Requires files:read scope.")]
    public async Task<CallToolResult> BatchStatAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute file or directory paths to check (1 to 100 entries)")] List<string> paths)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (paths is null || paths.Count < 1 || paths.Count > 100)
            return CreateErrorResult("INVALID_REQUEST", "paths must contain between 1 and 100 entries.");

        var command = new BatchStatCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Paths = paths.ToList()
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<BatchStatResult>(command, "fs_batch_stat", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_move", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Moves or renames a file or directory on a target Windows agent device. Both source and destination must be within configured writable roots. Cross-volume moves are not supported. Requires files:write scope. Ask the user for confirmation before executing.")]
    public async Task<CallToolResult> MoveAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the source file or directory")] string path,
        [Description("The absolute path of the move destination")] string destination,
        [Description("Whether to overwrite the destination file if it already exists (default: false). Directory overwrite is never allowed.")] bool overwrite = false,
        [Description("Optional SHA-256 hex digest of the source file. If provided, the move is aborted when the actual hash does not match (concurrency guard, files only).")] string? expectedSha256 = null)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (string.IsNullOrWhiteSpace(destination))
            return CreateErrorResult("INVALID_REQUEST", "destination parameter is required.");

        var command = new MoveCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Destination = destination,
            Overwrite = overwrite,
            ExpectedSha256 = string.IsNullOrWhiteSpace(expectedSha256) ? null : expectedSha256
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<MoveResult>(command, "fs_move", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_copy", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Copies a file or a bounded directory tree to a new location on a target Windows agent device. Directory copy requires recursive=true, rejects merge and overwrite, and enforces entry and byte limits. The source must be within AllowedRoots and the destination within WritableRoots. Requires files:write scope. Ask the user for confirmation before executing.")]
    public async Task<CallToolResult> CopyAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the source file or directory to copy")] string path,
        [Description("The absolute path of the copy destination")] string destination,
        [Description("Whether to overwrite an existing destination file (default: false). Directory merge and overwrite are not supported.")] bool overwrite = false,
        [Description("Optional SHA-256 hex digest of the source file. Not supported for directory sources.")] string? expectedSourceSha256 = null,
        [Description("Whether to recursively include directory contents (default: false)")] bool recursive = false,
        [Description("Maximum number of descendant entries (default: 1000, hard limit: 5000)")] int maxEntries = 1000,
        [Description("Maximum total bytes to transfer (default: 104857600, hard limit: 1073741824)")] long maxTotalBytes = 104857600)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (string.IsNullOrWhiteSpace(destination))
            return CreateErrorResult("INVALID_REQUEST", "destination parameter is required.");

        if (maxEntries < 1 || maxEntries > 5000)
            return CreateErrorResult("INVALID_REQUEST", "maxEntries must be between 1 and 5000.");

        if (maxTotalBytes < 1 || maxTotalBytes > 1073741824)
            return CreateErrorResult("INVALID_REQUEST", "maxTotalBytes must be between 1 and 1073741824.");

        var command = new CopyCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Destination = destination,
            Overwrite = overwrite,
            ExpectedSourceSha256 = string.IsNullOrWhiteSpace(expectedSourceSha256) ? null : expectedSourceSha256,
            Recursive = recursive,
            MaxEntries = maxEntries,
            MaxTotalBytes = maxTotalBytes
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<CopyResult>(command, "fs_copy", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_delete", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Deletes a single file on a target Windows agent device. Directories are not supported. The path must be within configured writable roots. Requires files:write scope. Ask the user for confirmation before executing.")]
    public async Task<CallToolResult> DeleteAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the file to delete")] string path,
        [Description("Optional SHA-256 hex digest of the current file. If provided, deletion is aborted when the actual hash does not match.")] string? expectedSha256 = null,
        [Description("Whether a missing file should be treated as success (default: false)")] bool missingOk = false)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        var command = new DeleteCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            ExpectedSha256 = string.IsNullOrWhiteSpace(expectedSha256) ? null : expectedSha256,
            MissingOk = missingOk
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<DeleteResult>(command, "fs_delete", deviceId, cancellationToken);
    }

    [McpServerTool(Name = "fs_rmdir", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Removes one empty directory on a target Windows agent device. Recursive deletion is not supported. The path must be within configured writable roots and configured root directories cannot be removed. Requires files:write scope. Ask the user for confirmation before executing.")]
    public async Task<CallToolResult> RemoveDirectoryAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute path of the empty directory to remove")] string path,
        [Description("Whether a missing directory should be treated as success (default: false)")] bool missingOk = false)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        var command = new RemoveDirectoryCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            MissingOk = missingOk
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<RemoveDirectoryResult>(command, "fs_rmdir", deviceId, cancellationToken);
    }

    // ──────────────────────────────────────────────
    // Private transport and auth helpers
    // ──────────────────────────────────────────────

    private async Task<bool> AuthorizeScopeAsync(string policyName)
    {
        var principal = _httpContextAccessor?.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        var authResult = await _authorizationService.AuthorizeAsync(principal, null, policyName);
        return authResult.Succeeded;
    }

    private CancellationToken GetCancellationToken()
    {
        return _httpContextAccessor?.HttpContext?.RequestAborted ?? CancellationToken.None;
    }

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
