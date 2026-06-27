using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.Gateway.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalMcp.Gateway.Mcp;

[McpServerToolType]
public sealed class BatchReadTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<BatchReadTools> _logger;

    public BatchReadTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<BatchReadTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "fs_batch_read",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Reads between 1 and 20 UTF-8 text files in one bounded request. Each path is evaluated independently, input order is preserved, and per-file plus total response byte limits are enforced. Requires files:read scope.")]
    public async Task<CallToolResult> BatchReadAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The absolute UTF-8 text file paths to read (1 to 20 entries)")] List<string> paths,
        [Description("Maximum UTF-8 content bytes returned per file (default: 262144, hard limit: 1048576)")] int maxBytesPerFile = 262144,
        [Description("Maximum UTF-8 content bytes returned across the batch (default: 2097152, hard limit: 8388608)")] long maxTotalBytes = 2097152)
    {
        if (!await AuthorizeScopeAsync())
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        if (paths is null || paths.Count < 1 || paths.Count > 20)
            return CreateErrorResult("INVALID_REQUEST", "paths must contain between 1 and 20 entries.");

        if (maxBytesPerFile < 1 || maxBytesPerFile > 1048576)
            return CreateErrorResult("INVALID_REQUEST", "maxBytesPerFile must be between 1 and 1048576.");

        if (maxTotalBytes < 1 || maxTotalBytes > 8388608)
            return CreateErrorResult("INVALID_REQUEST", "maxTotalBytes must be between 1 and 8388608.");

        var command = new BatchReadCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Paths = paths.ToList(),
            MaxBytesPerFile = maxBytesPerFile,
            MaxTotalBytes = maxTotalBytes
        };

        try
        {
            var result = await _dispatcher.SendAsync<BatchReadResult>(command, GetCancellationToken());
            if (result.Success && result.Data is not null)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result.Data, JsonOptions.Default)
                    }],
                    IsError = false
                };
            }

            return CreateErrorResult(
                result.Error?.Code ?? "INTERNAL_ERROR",
                result.Error?.Message ?? "An unexpected error occurred during command execution.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing fs_batch_read for device {DeviceId}", deviceId);
            return CreateErrorResult("INTERNAL_ERROR", "An unexpected error occurred on the gateway.");
        }
    }

    private async Task<bool> AuthorizeScopeAsync()
    {
        var principal = _httpContextAccessor?.HttpContext?.User
            ?? new ClaimsPrincipal(new ClaimsIdentity());
        var authResult = await _authorizationService.AuthorizeAsync(
            principal,
            null,
            "FilesReadPolicy");
        return authResult.Succeeded;
    }

    private CancellationToken GetCancellationToken() =>
        _httpContextAccessor?.HttpContext?.RequestAborted ?? CancellationToken.None;

    private static CallToolResult CreateErrorResult(string code, string message) =>
        new()
        {
            Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
            IsError = true
        };
}
