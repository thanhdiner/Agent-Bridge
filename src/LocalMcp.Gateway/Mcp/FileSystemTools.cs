using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Globalization;
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
public sealed partial class FileSystemTools
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
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute path of the file to read")] string path)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        }

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        var command = new ReadFileCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<ReadFileResult>(command, "fs_read", deviceId ?? "", cancellationToken);
    }

    [McpServerTool(Name = "fs_read_range", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Reads a bounded range of lines from a UTF-8 text file on a target Windows agent device without loading the whole file into memory. Requires files:read scope.")]
    public async Task<CallToolResult> ReadRangeAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute path of the UTF-8 text file to read")] string path,
        [Description("The one-based line number at which to start reading (default: 1)")] long startLine = 1,
        [Description("The maximum number of lines to return (default: 200, hard limit: 1000)")] int lineCount = 200)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");

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
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            StartLine = startLine,
            LineCount = lineCount
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<ReadRangeResult>(command, "fs_read_range", deviceId ?? "", cancellationToken);
    }

    [McpServerTool(Name = "fs_tree", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Returns a bounded directory tree for a path inside an allowed root. Requires files:read scope.")]
    public async Task<CallToolResult> GetTreeAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute path of the root directory of the tree")] string path,
        [Description("The maximum depth of the tree (default: 4, hard limit: 10)")] int maxDepth = 4,
        [Description("The maximum number of entries to return (default: 1000, hard limit: 5000)")] int maxEntries = 1000)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        }

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (maxDepth < 1 || maxDepth > 10)
            return CreateErrorResult("INVALID_REQUEST", "maxDepth must be between 1 and 10.");

        if (maxEntries < 1 || maxEntries > 5000)
            return CreateErrorResult("INVALID_REQUEST", "maxEntries must be between 1 and 5000.");

        var command = new TreeCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            MaxDepth = maxDepth,
            MaxEntries = maxEntries,
            IncludeHidden = false
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<TreeResult>(command, "fs_tree", deviceId ?? "", cancellationToken);
    }

    [McpServerTool(Name = "fs_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Lists the immediate contents of a directory on a target Windows agent device. Requires files:read scope.")]
    public async Task<CallToolResult> ListDirectoryAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute path of the directory to list")] string path,
        [Description("The maximum number of entries to return (default: 1000, hard limit: 5000)")] int maxEntries = 1000)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        }

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (maxEntries < 1 || maxEntries > 5000)
            return CreateErrorResult("INVALID_REQUEST", "maxEntries must be between 1 and 5000.");

        var command = new ListDirectoryCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            MaxEntries = maxEntries
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<ListDirectoryResult>(command, "fs_list", deviceId ?? "", cancellationToken);
    }

    [McpServerTool(Name = "fs_search", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Recursively searches for files matching a pattern or query in a directory on a target Windows agent device. Requires files:read scope.")]
    public async Task<CallToolResult> SearchFilesAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute path of the directory to search in")] string path,
        [Description("The search query (filename or content search)")] string query,
        [Description("Maximum results to return (default: 100, hard limit: 500)")] int maxResults = 100,
        [Description("Maximum depth to search (default: 4, hard limit: 10)")] int maxDepth = 4)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        }

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
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Query = query,
            MaxResults = maxResults,
            MaxDepth = maxDepth
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<SearchFilesResult>(command, "fs_search", deviceId ?? "", cancellationToken);
    }

    [McpServerTool(Name = "fs_search_context", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Searches UTF-8 text files and returns matching lines, bounded surrounding context, and each file's SHA-256 so results can be patched safely without a separate read. Supports literal or regex queries plus include/exclude globs. Requires files:read scope.")]
    public async Task<CallToolResult> SearchContextAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute path of the directory to search in")] string path,
        [Description("The literal text or regular expression to search for")] string query,
        [Description("Whether query should be interpreted as a regular expression (default: false)")] bool useRegex = false,
        [Description("Whether matching should be case-sensitive (default: false)")] bool caseSensitive = false,
        [Description("Optional file globs to include, such as **/*.cs. Empty includes all files")] List<string>? includeGlobs = null,
        [Description("Optional file or directory globs to exclude, such as **/bin/**")] List<string>? excludeGlobs = null,
        [Description("Number of lines to return before each match (default: 2, hard limit: 10)")] int contextBefore = 2,
        [Description("Number of lines to return after each match (default: 2, hard limit: 10)")] int contextAfter = 2,
        [Description("Maximum matches to return (default: 100, hard limit: 500)")] int maxResults = 100,
        [Description("Maximum directory depth to search (default: 4, hard limit: 10)")] int maxDepth = 4)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");
        if (string.IsNullOrEmpty(query))
            return CreateErrorResult("SEARCH_QUERY_REQUIRED", "Search query is required.");
        if (contextBefore < 0 || contextBefore > 10 || contextAfter < 0 || contextAfter > 10)
            return CreateErrorResult("INVALID_REQUEST", "contextBefore and contextAfter must be between 0 and 10.");
        if (maxResults < 1 || maxResults > 500)
            return CreateErrorResult("INVALID_REQUEST", "maxResults must be between 1 and 500.");
        if (maxDepth < 1 || maxDepth > 10)
            return CreateErrorResult("INVALID_REQUEST", "maxDepth must be between 1 and 10.");
        if (includeGlobs is { Count: > 20 } || excludeGlobs is { Count: > 20 })
            return CreateErrorResult("INVALID_REQUEST", "includeGlobs and excludeGlobs may contain at most 20 patterns each.");
        if ((includeGlobs?.Any(pattern => string.IsNullOrWhiteSpace(pattern) || pattern.Length > 256) ?? false) ||
            (excludeGlobs?.Any(pattern => string.IsNullOrWhiteSpace(pattern) || pattern.Length > 256) ?? false))
            return CreateErrorResult("INVALID_REQUEST", "Glob patterns must be non-empty and at most 256 characters.");

        var command = new SearchContextCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Query = query,
            UseRegex = useRegex,
            CaseSensitive = caseSensitive,
            IncludeGlobs = includeGlobs?.ToList() ?? [],
            ExcludeGlobs = excludeGlobs?.ToList() ?? [],
            ContextBefore = contextBefore,
            ContextAfter = contextAfter,
            MaxResults = maxResults,
            MaxDepth = maxDepth
        };

        return await DispatchAsync<SearchContextResult>(command, "fs_search_context", deviceId ?? "", GetCancellationToken());
    }

    [McpServerTool(Name = "git_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Returns bounded Git working-tree status for a repository on the target Windows agent. Repository paths and reported entries are filtered through the file-access policy. Requires Git on the agent and files:read scope.")]
    public async Task<CallToolResult> GitStatusAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("An absolute directory path inside the Git working tree")] string path,
        [Description("Whether untracked files should be included (default: true)")] bool includeUntracked = true,
        [Description("Maximum status entries to return (default: 1000, hard limit: 5000)")] int maxEntries = 1000)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");
        if (maxEntries < 1 || maxEntries > 5000)
            return CreateErrorResult("INVALID_REQUEST", "maxEntries must be between 1 and 5000.");

        var command = new GitStatusCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            IncludeUntracked = includeUntracked,
            MaxEntries = maxEntries
        };

        return await DispatchAsync<GitStatusResult>(command, "git_status", deviceId ?? "", GetCancellationToken());
    }

    [McpServerTool(Name = "git_diff", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Returns a bounded, no-color Git diff for authorized repository files. External diff drivers, text conversion, pagers, prompts, fsmonitor, and submodule recursion are disabled. Optionally includes safe synthetic patches for untracked UTF-8 files. Requires Git on the agent and files:read scope.")]
    public async Task<CallToolResult> GitDiffAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("An absolute directory path inside the Git working tree")] string path,
        [Description("Whether to return the staged diff instead of the unstaged diff (default: false)")] bool staged = false,
        [Description("Whether to append untracked UTF-8 files when staged is false (default: true)")] bool includeUntracked = true,
        [Description("Optional Git pathspecs used to narrow the diff (maximum 100)")] List<string>? pathSpecs = null,
        [Description("Unified diff context lines (default: 3, hard limit: 20)")] int contextLines = 3,
        [Description("Maximum UTF-8 diff bytes returned (default: 1048576, hard limit: 4194304)")] int maxBytes = 1_048_576)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");
        if (contextLines < 0 || contextLines > 20)
            return CreateErrorResult("INVALID_REQUEST", "contextLines must be between 0 and 20.");
        if (maxBytes < 1 || maxBytes > 4_194_304)
            return CreateErrorResult("INVALID_REQUEST", "maxBytes must be between 1 and 4194304.");
        if (pathSpecs is { Count: > 100 } ||
            (pathSpecs?.Any(spec => string.IsNullOrWhiteSpace(spec) || spec.Length > 512) ?? false) ||
            (pathSpecs?.Sum(spec => spec.Length) ?? 0) > 16_384)
        {
            return CreateErrorResult(
                "INVALID_REQUEST",
                "pathSpecs may contain at most 100 non-empty entries of at most 512 characters and 16384 characters in total.");
        }

        var command = new GitDiffCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Staged = staged,
            IncludeUntracked = includeUntracked,
            PathSpecs = pathSpecs?.ToList() ?? [],
            ContextLines = contextLines,
            MaxBytes = maxBytes
        };

        return await DispatchAsync<GitDiffResult>(command, "git_diff", deviceId ?? "", GetCancellationToken());
    }

    [McpServerTool(Name = "git_log", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Returns bounded Git commit history with optional author, ISO-date, literal path, and short-stat filters. Repository paths are filtered through the file-access policy. Requires Git on the agent and files:read scope.")]
    public async Task<CallToolResult> GitLogAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("An absolute directory path inside the Git working tree")] string path,
        [Description("Maximum commits to return (default: 20, hard limit: 100)")] int maxCount = 20,
        [Description("Number of matching commits to skip (default: 0)")] int skip = 0,
        [Description("Optional authorized repository-relative literal path used to filter history")] string? pathSpec = null,
        [Description("Optional Git author pattern (maximum 256 characters)")] string? author = null,
        [Description("Optional inclusive lower authored-date bound in ISO format")] string? since = null,
        [Description("Optional inclusive upper authored-date bound in ISO format")] string? until = null,
        [Description("Whether to include files-changed, insertion, and deletion totals (default: false)")] bool includeStats = false)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");
        if (maxCount is < 1 or > 100)
            return CreateErrorResult("INVALID_REQUEST", "maxCount must be between 1 and 100.");
        if (skip is < 0 or > 1_000_000)
            return CreateErrorResult("INVALID_REQUEST", "skip must be between 0 and 1000000.");
        if (pathSpec is not null && (string.IsNullOrWhiteSpace(pathSpec) || pathSpec.Length > 512 || pathSpec.Contains('\0')))
            return CreateErrorResult("INVALID_REQUEST", "pathSpec must be non-empty and at most 512 characters when provided.");
        if (author is not null && (author.Length > 256 || author.Contains('\0')))
            return CreateErrorResult("INVALID_REQUEST", "author must be at most 256 characters.");
        if (!IsValidIsoDate(since))
            return CreateErrorResult("INVALID_REQUEST", "since must be a valid ISO date.");
        if (!IsValidIsoDate(until))
            return CreateErrorResult("INVALID_REQUEST", "until must be a valid ISO date.");

        var command = new GitLogCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            MaxCount = maxCount,
            Skip = skip,
            PathSpec = pathSpec,
            Author = author,
            Since = since,
            Until = until,
            IncludeStats = includeStats
        };

        return await DispatchAsync<GitLogResult>(command, "git_log", deviceId ?? "", GetCancellationToken());
    }

    [McpServerTool(Name = "git_show", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Returns metadata, optional statistics, and a bounded patch for one Git commit. Only policy-authorized repository files are included. External diff drivers, text conversion, pagers, prompts, fsmonitor, and submodule recursion are disabled. Requires Git on the agent and files:read scope.")]
    public async Task<CallToolResult> GitShowAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("An absolute directory path inside the Git working tree")] string path,
        [Description("Commit revision such as a hash, HEAD, or HEAD~1")] string revision,
        [Description("Optional Git pathspecs used to narrow the commit (maximum 100)")] List<string>? pathSpecs = null,
        [Description("Whether to include the unified patch (default: true)")] bool includePatch = true,
        [Description("Whether to include files-changed, insertion, and deletion totals (default: true)")] bool includeStats = true,
        [Description("Unified diff context lines (default: 3, hard limit: 20)")] int contextLines = 3,
        [Description("Maximum UTF-8 patch bytes returned (default: 1048576, hard limit: 4194304)")] int maxBytes = 1_048_576)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");
        if (string.IsNullOrWhiteSpace(revision) || revision.Length > 256 || revision.Any(char.IsControl))
            return CreateErrorResult("INVALID_REQUEST", "revision is required and must be at most 256 characters without control characters.");
        if (contextLines is < 0 or > 20)
            return CreateErrorResult("INVALID_REQUEST", "contextLines must be between 0 and 20.");
        if (maxBytes is < 1 or > 4_194_304)
            return CreateErrorResult("INVALID_REQUEST", "maxBytes must be between 1 and 4194304.");
        if (pathSpecs is { Count: > 100 } ||
            (pathSpecs?.Any(spec => string.IsNullOrWhiteSpace(spec) || spec.Length > 512 || spec.Contains('\0')) ?? false) ||
            (pathSpecs?.Sum(spec => spec.Length) ?? 0) > 16_384)
        {
            return CreateErrorResult(
                "INVALID_REQUEST",
                "pathSpecs may contain at most 100 non-empty entries of at most 512 characters and 16384 characters in total.");
        }

        var command = new GitShowCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Revision = revision,
            PathSpecs = pathSpecs?.ToList() ?? [],
            IncludePatch = includePatch,
            IncludeStats = includeStats,
            ContextLines = contextLines,
            MaxBytes = maxBytes
        };

        return await DispatchAsync<GitShowResult>(command, "git_show", deviceId ?? "", GetCancellationToken());
    }

    [McpServerTool(Name = "git_restore_file", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Restores a regular tracked file from HEAD into the working tree on a target Windows agent device. Does not modify the Git index/staging. Requires Git on the agent and files:write scope.")]
    public async Task<CallToolResult> GitRestoreFileAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("An absolute directory path inside the Git working tree")] string path,
        [Description("Exactly one repository-relative literal file path to restore")] string pathSpec,
        [Description("Optional expected SHA-256 hash of the current file content as a concurrency guard")] string? expectedSha256 = null)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");
        }
        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");
        if (string.IsNullOrWhiteSpace(pathSpec))
            return CreateErrorResult("INVALID_REQUEST", "pathSpec parameter is required.");

        var command = new GitRestoreFileCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            PathSpec = pathSpec,
            ExpectedSha256 = expectedSha256
        };

        return await DispatchAsync<GitRestoreFileResult>(command, "git_restore_file", deviceId ?? "", GetCancellationToken());
    }

    [McpServerTool(Name = "git_refresh_index", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Refreshes the Git index for a single regular tracked file on a target Windows agent device, updating out-of-sync stat cache or line ending attributes if semantic content matches the index. Requires Git on the agent and files:write scope.")]
    public async Task<CallToolResult> GitRefreshIndexAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("An absolute directory path inside the Git working tree")] string path,
        [Description("Exactly one repository-relative literal file path to refresh")] string pathSpec)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");
        }
        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");
        if (string.IsNullOrWhiteSpace(pathSpec))
            return CreateErrorResult("INVALID_REQUEST", "pathSpec parameter is required.");

        var command = new GitRefreshIndexCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            PathSpec = pathSpec
        };

        return await DispatchAsync<GitRefreshIndexResult>(command, "git_refresh_index", deviceId ?? "", GetCancellationToken());
    }

    [McpServerTool(Name = "project_verify", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true), Description("Detects a supported project type and runs fixed build, test, lint, or typecheck steps on the target Windows agent. Supports .NET, Node.js, Rust, PHP/Laravel, Python, and Go projects. This executes project-defined code and may generate build artifacts. Requires dev:execute scope. Ask the user for confirmation before executing.")]
    public async Task<CallToolResult> ProjectCheckAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("An absolute project directory inside an allowed root")] string path,
        [Description("Project type: auto, dotnet, node, rust, php, python, or go (default: auto)")] string projectType = "auto",
        [Description("Verification steps chosen from build, test, lint, and typecheck (default: build and test)")] List<string>? steps = null,
        [Description("Build configuration: Debug or Release (default: Debug)")] string configuration = "Debug",
        [Description("Overall execution timeout in seconds (default: 300, hard limit: 900)")] int timeoutSeconds = 300,
        [Description("Maximum UTF-8 output bytes returned across all steps (default: 1048576, hard limit: 4194304)")] int maxOutputBytes = 1_048_576)
    {
        if (!await AuthorizeScopeAsync("DevExecutePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        var normalizedProjectType = projectType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedProjectType is not ("auto" or "dotnet" or "node" or "rust" or "php" or "python" or "go"))
            return CreateErrorResult("INVALID_REQUEST", "projectType must be auto, dotnet, node, rust, php, python, or go.");

        var requestedSteps = steps?
            .Select(step => step?.Trim().ToLowerInvariant() ?? string.Empty)
            .ToList() ?? ["build", "test"];

        if (requestedSteps.Count is < 1 or > 4 ||
            requestedSteps.Any(step => step is not ("build" or "test" or "lint" or "typecheck")) ||
            requestedSteps.Distinct(StringComparer.Ordinal).Count() != requestedSteps.Count)
        {
            return CreateErrorResult(
                "INVALID_REQUEST",
                "steps must contain between 1 and 4 unique values chosen from build, test, lint, and typecheck.");
        }

        if (!string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase))
            return CreateErrorResult("INVALID_REQUEST", "configuration must be Debug or Release.");
        if (timeoutSeconds is < 30 or > 900)
            return CreateErrorResult("INVALID_REQUEST", "timeoutSeconds must be between 30 and 900.");
        if (maxOutputBytes is < 1024 or > 4_194_304)
            return CreateErrorResult("INVALID_REQUEST", "maxOutputBytes must be between 1024 and 4194304.");

        var command = new ProjectCheckCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            ProjectType = normalizedProjectType,
            Steps = requestedSteps,
            Configuration = string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug",
            TimeoutSeconds = timeoutSeconds,
            MaxOutputBytes = maxOutputBytes
        };

        return await DispatchAsync<ProjectVerifyResult>(
            command,
            "project_verify",
            deviceId ?? "",
            GetCancellationToken());
    }

    [McpServerTool(Name = "powershell_exec", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true), Description("Runs a bounded PowerShell 7 script on a target Windows agent. The working directory must be inside both AllowedRoots and WritableRoots. Child commands use the normal OS permissions of the Agent account and are not filesystem-sandboxed by LocalMcp roots. PowerShell profiles, interactive input, inherited secret-like environment variables, and execution from an elevated Agent are disabled. Requires dev:execute scope. Ask the user for confirmation before executing.")]
    public async Task<CallToolResult> PowerShellExecuteAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("An absolute working directory inside both AllowedRoots and WritableRoots")] string workingDirectory,
        [Description("The PowerShell 7 script to execute (maximum 65536 characters)")] string script,
        [Description("Execution timeout in seconds (default: 120, hard limit: 900)")] int timeoutSeconds = 120,
        [Description("Maximum combined UTF-8 stdout and stderr bytes returned (default: 1048576, hard limit: 4194304)")] int maxOutputBytes = 1_048_576)
    {
        if (!await AuthorizeScopeAsync("DevExecutePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return CreateErrorResult("INVALID_REQUEST", "workingDirectory parameter is required.");
        if (string.IsNullOrWhiteSpace(script) ||
            script.Length > 65_536 ||
            script.Contains('\0'))
        {
            return CreateErrorResult(
                "INVALID_REQUEST",
                "script must be non-empty, contain no NUL characters, and be at most 65536 characters.");
        }
        if (timeoutSeconds is < 1 or > 900)
            return CreateErrorResult("INVALID_REQUEST", "timeoutSeconds must be between 1 and 900.");
        if (maxOutputBytes is < 1024 or > 4_194_304)
            return CreateErrorResult("INVALID_REQUEST", "maxOutputBytes must be between 1024 and 4194304.");

        var command = new PowerShellExecuteCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkingDirectory = workingDirectory,
            Script = script,
            Visible = false,
            Elevated = false,
            TimeoutSeconds = timeoutSeconds,
            MaxOutputBytes = maxOutputBytes
        };

        return await DispatchAsync<PowerShellExecuteResult>(
            command,
            "powershell_exec",
            deviceId ?? "",
            GetCancellationToken());
    }

    [McpServerTool(Name = "powershell_exec_visible", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true), Description("Runs a bounded PowerShell 7 script in a visible console window on a target Windows agent. The working directory must be inside both AllowedRoots and WritableRoots. Child commands use normal OS permissions and are not filesystem-sandboxed by LocalMcp roots. Set elevated=true to request UAC elevation; the user must approve the Windows prompt. PowerShell profiles and inherited secret-like environment variables are disabled. Console prompts and child installer windows may require manual user interaction. Requires dev:execute scope. Ask the user for confirmation before executing.")]
    public async Task<CallToolResult> PowerShellExecuteVisibleAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("An absolute working directory inside both AllowedRoots and WritableRoots")] string workingDirectory,
        [Description("The PowerShell 7 script to execute (maximum 65536 characters)")] string script,
        [Description("Whether to request UAC elevation for the visible console (default: false)")] bool elevated = false,
        [Description("Execution timeout in seconds (default: 120, hard limit: 900)")] int timeoutSeconds = 120,
        [Description("Maximum combined UTF-8 output bytes returned (default: 1048576, hard limit: 4194304)")] int maxOutputBytes = 1_048_576)
    {
        if (!await AuthorizeScopeAsync("DevExecutePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return CreateErrorResult("INVALID_REQUEST", "workingDirectory parameter is required.");
        if (string.IsNullOrWhiteSpace(script) ||
            script.Length > 65_536 ||
            script.Contains('\0'))
        {
            return CreateErrorResult(
                "INVALID_REQUEST",
                "script must be non-empty, contain no NUL characters, and be at most 65536 characters.");
        }
        if (timeoutSeconds is < 1 or > 900)
            return CreateErrorResult("INVALID_REQUEST", "timeoutSeconds must be between 1 and 900.");
        if (maxOutputBytes is < 1024 or > 4_194_304)
            return CreateErrorResult("INVALID_REQUEST", "maxOutputBytes must be between 1024 and 4194304.");

        var command = new PowerShellExecuteCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkingDirectory = workingDirectory,
            Script = script,
            Visible = true,
            Elevated = elevated,
            TimeoutSeconds = timeoutSeconds,
            MaxOutputBytes = maxOutputBytes
        };

        return await DispatchAsync<PowerShellExecuteResult>(
            command,
            "powershell_exec_visible",
            deviceId ?? "",
            GetCancellationToken());
    }

    [McpServerTool(Name = "fs_write", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Creates a new UTF-8 text file or replaces the complete content of an existing text file on a target Windows agent device. Requires files:write scope. Safe workflow: (1) Call fs_read first; (2) Inspect content; (3) Pass returned sha256 as expectedSha256; (4) Call fs_write. Re-read on conflict.")]
    public async Task<CallToolResult> WriteFileAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute path of the file to write")] string path,
        [Description("The text content to write to the file")] string content,
        [Description("The expected SHA-256 hash of the existing file. Required if the file already exists.")] string? expectedSha256 = null,
        [Description("Whether to create the file if it does not exist (default: false)")] bool createIfMissing = false)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");
        }

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (content == null)
            return CreateErrorResult("INVALID_REQUEST", "content parameter is required.");

        var command = new WriteFileCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Content = content,
            ExpectedSha256 = expectedSha256,
            CreateIfMissing = createIfMissing
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<WriteFileResult>(command, "fs_write", deviceId ?? "", cancellationToken);
    }

    [McpServerTool(Name = "fs_patch", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Applies one or more exact text replacements to an existing UTF-8 text file on a target Windows agent device. Requires files:write scope. Safe workflow: (1) Call fs_read first; (2) Inspect content; (3) Pass returned sha256 as expectedSha256; (4) Call fs_patch. Re-read on conflict.")]
    public async Task<CallToolResult> PatchFileAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute path of the file to patch")] string path,
        [Description("The expected SHA-256 hash of the current file content")] string expectedSha256,
        [Description("The list of exact text replacements to apply")] List<PatchEdit> edits)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");
        }

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (string.IsNullOrEmpty(expectedSha256))
            return CreateErrorResult("EXPECTED_HASH_REQUIRED", "expectedSha256 parameter is required.");

        if (edits == null || edits.Count == 0)
            return CreateErrorResult("PATCH_EDITS_REQUIRED", "edits parameter is required and cannot be empty.");

        var command = new PatchFileCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            ExpectedSha256 = expectedSha256,
            Edits = edits
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<PatchFileResult>(command, "fs_patch", deviceId ?? "", cancellationToken);
    }

    [McpServerTool(Name = "fs_batch_patch", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Applies exact text replacements to 1-20 UTF-8 files with ordered per-item results. Requires files:write scope.")]
    public async Task<CallToolResult> BatchPatchAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The file patch requests to execute (1 to 20 entries)")] List<MultiFilePatchItem> items)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");

        if (items is null || items.Count < 1 || items.Count > 20)
            return CreateErrorResult("INVALID_REQUEST", "items must contain between 1 and 20 entries.");
        var command = new MultiFilePatchCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Items = items.ToList()
        };

        return await DispatchAsync<MultiFilePatchResult>(command, "fs_batch_patch", deviceId ?? "", GetCancellationToken());
    }

    [McpServerTool(Name = "fs_mkdir", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Creates a directory or directories at the specified path on a target Windows agent device. Requires files:write scope. Recursive creation is supported.")]
    public async Task<CallToolResult> CreateDirectoryAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute path of the directory to create")] string path,
        [Description("Whether to recursively create parent directories if missing (default: false)")] bool recursive = false)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");
        }

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        var command = new CreateDirectoryCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Recursive = recursive
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<CreateDirectoryResult>(command, "fs_mkdir", deviceId ?? "", cancellationToken);
    }

    [McpServerTool(Name = "fs_stat", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Gets file or directory status metadata (exists, size, type, sha256) on a target Windows agent device. Requires files:read scope.")]
    public async Task<CallToolResult> StatAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute path of the file or directory to check")] string path)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");
        }

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        var command = new StatCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<StatResult>(command, "fs_stat", deviceId ?? "", cancellationToken);
    }

    [McpServerTool(Name = "fs_batch_stat", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Gets status metadata for between 1 and 100 file or directory paths on a target Windows agent device. Each path is evaluated independently and input order is preserved. Requires files:read scope.")]
    public async Task<CallToolResult> BatchStatAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute file or directory paths to check (1 to 100 entries)")] List<string> paths)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");

        if (paths is null || paths.Count < 1 || paths.Count > 100)
            return CreateErrorResult("INVALID_REQUEST", "paths must contain between 1 and 100 entries.");

        var command = new BatchStatCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Paths = paths.ToList()
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<BatchStatResult>(command, "fs_batch_stat", deviceId ?? "", cancellationToken);
    }

    [McpServerTool(Name = "fs_move", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Moves or renames a file or directory on a target Windows agent device. File moves support cross-volume copy-verify-delete fallback; directory moves remain same-volume only. Both source and destination must be within configured writable roots. Requires files:write scope. Ask the user for confirmation before executing.")]
    public async Task<CallToolResult> MoveAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute path of the source file or directory")] string path,
        [Description("The absolute path of the move destination")] string destination,
        [Description("Whether to overwrite the destination file if it already exists (default: false). Directory overwrite is never allowed.")] bool overwrite = false,
        [Description("Optional SHA-256 hex digest of the source file. If provided, the move is aborted when the actual hash does not match (concurrency guard, files only).")] string? expectedSha256 = null)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        if (string.IsNullOrWhiteSpace(destination))
            return CreateErrorResult("INVALID_REQUEST", "destination parameter is required.");

        var command = new MoveCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Destination = destination,
            Overwrite = overwrite,
            ExpectedSha256 = string.IsNullOrWhiteSpace(expectedSha256) ? null : expectedSha256
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<MoveResult>(command, "fs_move", deviceId ?? "", cancellationToken);
    }

    [McpServerTool(Name = "fs_copy", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Copies a file or a bounded directory tree to a new location on a target Windows agent device. Directory copy requires recursive=true, rejects merge and overwrite, and enforces entry and byte limits. The source must be within AllowedRoots and the destination within WritableRoots. Requires files:write scope. Ask the user for confirmation before executing.")]
    public async Task<CallToolResult> CopyAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
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
            DeviceId = deviceId ?? "",
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
        return await DispatchAsync<CopyResult>(command, "fs_copy", deviceId ?? "", cancellationToken);
    }

    [McpServerTool(Name = "fs_delete", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Deletes a single file on a target Windows agent device. Directories are not supported. The path must be within configured writable roots. Requires files:write scope. Ask the user for confirmation before executing.")]
    public async Task<CallToolResult> DeleteAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute path of the file to delete")] string path,
        [Description("Optional SHA-256 hex digest of the current file. If provided, deletion is aborted when the actual hash does not match.")] string? expectedSha256 = null,
        [Description("Whether a missing file should be treated as success (default: false)")] bool missingOk = false)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        var command = new DeleteCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            ExpectedSha256 = string.IsNullOrWhiteSpace(expectedSha256) ? null : expectedSha256,
            MissingOk = missingOk
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<DeleteResult>(command, "fs_delete", deviceId ?? "", cancellationToken);
    }

    [McpServerTool(Name = "fs_rmdir", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Removes one empty directory on a target Windows agent device. Recursive deletion is not supported. The path must be within configured writable roots and configured root directories cannot be removed. Requires files:write scope. Ask the user for confirmation before executing.")]
    public async Task<CallToolResult> RemoveDirectoryAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute path of the empty directory to remove")] string path,
        [Description("Whether a missing directory should be treated as success (default: false)")] bool missingOk = false)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");

        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");

        var command = new RemoveDirectoryCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            MissingOk = missingOk
        };

        var cancellationToken = GetCancellationToken();
        return await DispatchAsync<RemoveDirectoryResult>(command, "fs_rmdir", deviceId ?? "", cancellationToken);
    }

    // ──────────────────────────────────────────────
    // Private transport and auth helpers
    // ──────────────────────────────────────────────

    private static bool IsValidIsoDate(string? value) =>
        value is null ||
        (value.Length <= 64 && DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _));

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
        string? deviceId,
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
            return CreateErrorResult(errorCode, errorMessage, result.Error?.Details);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing {ToolName} for device {DeviceId}", toolName, deviceId);
            return CreateErrorResult("INTERNAL_ERROR", "An unexpected error occurred on the gateway.");
        }
    }

    private static CallToolResult CreateErrorResult(
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? details = null)
    {
        var text = details is null
            ? $"Error [{code}]: {message}"
            : JsonSerializer.Serialize(
                new
                {
                    error = new
                    {
                        code,
                        message,
                        details
                    }
                },
                JsonOptions.Default);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            IsError = true
        };
    }
}


