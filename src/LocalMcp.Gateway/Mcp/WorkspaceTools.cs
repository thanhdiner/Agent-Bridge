using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.Gateway.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalMcp.Gateway.Mcp;

[McpServerToolType]
public sealed class WorkspaceTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<WorkspaceTools> _logger;

    public WorkspaceTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<WorkspaceTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "workspace_list",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Lists configured workspace aliases on a target Windows agent, including availability and effective read/write access. Requires files:read scope.")]
    public async Task<CallToolResult> ListAsync(
        [Description("The unique identifier of the target agent device")] string deviceId)
    {
        if (!await AuthorizeScopeAsync("FilesReadPolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:read");

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        var command = new WorkspaceListCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        return await DispatchAsync<WorkspaceListResult>(
            command,
            "workspace_list",
            deviceId,
            GetCancellationToken());
    }

    [McpServerTool(
        Name = "workspace_resolve",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Resolves a workspace alias plus a relative path into a canonical absolute path on a target Windows agent. Traversal outside the workspace is rejected. Set requireWritable=true before write operations. Requires files:read or files:write scope.")]
    public async Task<CallToolResult> ResolveAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("Configured workspace alias, for example main or work")] string workspace,
        [Description("Relative path inside the workspace. Empty resolves the workspace root.")] string? relativePath = null,
        [Description("Whether the selected workspace must have effective write permission (default: false)")] bool requireWritable = false)
    {
        var policy = requireWritable ? "FilesWritePolicy" : "FilesReadPolicy";
        var requiredScope = requireWritable ? "files:write" : "files:read";
        if (!await AuthorizeScopeAsync(policy))
            return CreateErrorResult("FORBIDDEN", $"Access denied. Required scope: {requiredScope}");

        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");
        if (string.IsNullOrWhiteSpace(workspace))
            return CreateErrorResult("INVALID_REQUEST", "workspace parameter is required.");
        if (workspace.Length > 64)
            return CreateErrorResult("INVALID_REQUEST", "workspace must be at most 64 characters.");
        if (relativePath is { Length: > 32768 })
            return CreateErrorResult("INVALID_REQUEST", "relativePath must be at most 32768 characters.");

        var command = new WorkspaceResolveCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Alias = workspace,
            RelativePath = relativePath,
            RequireWritable = requireWritable
        };

        return await DispatchAsync<WorkspaceResolveResult>(
            command,
            "workspace_resolve",
            deviceId,
            GetCancellationToken());
    }

    private async Task<bool> AuthorizeScopeAsync(string policyName)
    {
        var principal = _httpContextAccessor?.HttpContext?.User ??
            new ClaimsPrincipal(new ClaimsIdentity());
        var authResult = await _authorizationService.AuthorizeAsync(
            principal,
            null,
            policyName);
        return authResult.Succeeded;
    }

    private CancellationToken GetCancellationToken() =>
        _httpContextAccessor?.HttpContext?.RequestAborted ?? CancellationToken.None;

    private async Task<CallToolResult> DispatchAsync<TResult>(
        AgentCommand command,
        string toolName,
        string deviceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _dispatcher.SendAsync<TResult>(command, cancellationToken);
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
                result.Error?.Message ?? "Workspace command failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error executing {ToolName} for device {DeviceId}",
                toolName,
                deviceId);
            return CreateErrorResult(
                "INTERNAL_ERROR",
                "An unexpected error occurred on the gateway.");
        }
    }

    private static CallToolResult CreateErrorResult(string code, string message) => new()
    {
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
        IsError = true
    };
}
