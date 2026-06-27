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
