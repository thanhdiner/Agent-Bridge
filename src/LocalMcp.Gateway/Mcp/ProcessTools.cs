using System.ComponentModel;
using System.Runtime.InteropServices;
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
public sealed class ProcessTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<ProcessTools> _logger;

    public ProcessTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<ProcessTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "process_list",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Lists live Windows processes with bounded metadata and optional name filtering. Requires dev:execute scope.")]
    public async Task<CallToolResult> ListAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Optional case-insensitive substring filter for the process name")] string? nameContains = null,
        [Description("Whether to include processes without a top-level window (default: true)")] bool includeWindowless = true,
        [Description("Maximum processes returned (default: 200, hard limit: 1000)")] int maxResults = 200)
    {
        var authorizationError = await ValidateAuthorizationAndDeviceAsync(deviceId);
        if (authorizationError is not null)
            return authorizationError;
        if (nameContains is not null && (nameContains.Length > 260 || nameContains.Any(char.IsControl)))
            return Error("INVALID_REQUEST", "nameContains must be at most 260 characters without control characters.");
        if (maxResults is < 1 or > 1_000)
            return Error("INVALID_REQUEST", "maxResults must be between 1 and 1000.");

        return await DispatchAsync<ProcessListResult>(
            new ProcessListCommand
            {
                CommandId = Guid.NewGuid(),
                DeviceId = deviceId ?? "",
                CreatedAt = DateTimeOffset.UtcNow,
                NameContains = nameContains,
                IncludeWindowless = includeWindowless,
                MaxResults = maxResults
            },
            "process_list");
    }

    [McpServerTool(
        Name = "process_kill",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false),
     Description("Immediately terminates one exact Windows process by PID. Supports an expected process-name guard against PID reuse and refuses protected system or agent processes. Requires dev:execute scope.")]
    public async Task<CallToolResult> KillAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Exact process ID to terminate")] int processId,
        [Description("Optional exact process name guard, with or without .exe, to prevent killing a reused PID")] string? expectedProcessName = null,
        [Description("Whether to terminate the entire child process tree (default: true)")] bool entireProcessTree = true,
        [Description("Maximum milliseconds to wait for process exit (default: 5000, hard limit: 300000)")] int timeoutMs = 5_000)
    {
        var authorizationError = await ValidateAuthorizationAndDeviceAsync(deviceId);
        if (authorizationError is not null)
            return authorizationError;
        if (processId <= 0)
            return Error("INVALID_REQUEST", "processId must be greater than zero.");
        if (expectedProcessName is not null
            && (string.IsNullOrWhiteSpace(expectedProcessName)
                || expectedProcessName.Length > 260
                || expectedProcessName.Any(char.IsControl)))
        {
            return Error("INVALID_REQUEST", "expectedProcessName is invalid.");
        }
        if (timeoutMs is < 1 or > 300_000)
            return Error("INVALID_REQUEST", "timeoutMs must be between 1 and 300000.");

        return await DispatchAsync<ProcessKillResult>(
            new ProcessKillCommand
            {
                CommandId = Guid.NewGuid(),
                DeviceId = deviceId ?? "",
                CreatedAt = DateTimeOffset.UtcNow,
                ProcessId = processId,
                ExpectedProcessName = expectedProcessName,
                EntireProcessTree = entireProcessTree,
                TimeoutMs = timeoutMs
            },
            "process_kill");
    }

    private async Task<CallToolResult?> ValidateAuthorizationAndDeviceAsync(string? deviceId)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        return null;
    }

    private async Task<CallToolResult> DispatchAsync<TResult>(AgentCommand command, string toolName)
    {
        try
        {
            var result = await _dispatcher.SendAsync<TResult>(command, RequestToken());
            if (result.Success && result.Data is not null)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result.Data, JsonOptions.Default) }],
                    IsError = false
                };
            }

            return Error(result.Error?.Code ?? "INTERNAL_ERROR", result.Error?.Message ?? "Command execution failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected {ToolName} failure for device {DeviceId}", toolName, command.DeviceId);
            return Error("INTERNAL_ERROR", "An unexpected gateway error occurred.");
        }
    }

    private async Task<bool> AuthorizedAsync()
    {
        var principal = _httpContextAccessor?.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        return (await _authorizationService.AuthorizeAsync(principal, null, "DevExecutePolicy")).Succeeded;
    }

    private CancellationToken RequestToken() =>
        _httpContextAccessor?.HttpContext?.RequestAborted ?? CancellationToken.None;

    private static CallToolResult Error(string code, string message) => new()
    {
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
        IsError = true
    };
}


