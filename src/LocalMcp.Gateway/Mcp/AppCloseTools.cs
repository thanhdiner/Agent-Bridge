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
public sealed class AppCloseTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<AppCloseTools> _logger;

    public AppCloseTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<AppCloseTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "app_close",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false),
     Description("Closes a Windows application by process id or exact process name. Graceful close is attempted first; force=true allows process termination as fallback. Multiple same-name processes require allMatches=true. Requires dev:execute scope.")]
    public async Task<CallToolResult> CloseAppAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent.")] string? deviceId,
        [Description("Optional exact Windows process id. Use with processName as a PID-reuse guard")] int? processId = null,
        [Description("Optional exact process name, with or without .exe, such as notepad or chrome")] string? processName = null,
        [Description("Close every process with the exact processName when more than one matches (default: false)")] bool allMatches = false,
        [Description("Force terminate processes that do not exit after a graceful close attempt (default: false)")] bool force = false,
        [Description("When force=true, terminate the complete descendant process tree (default: false)")] bool entireProcessTree = false,
        [Description("Maximum total close wait in milliseconds (default: 5000, hard limit: 300000)")] int timeoutMs = 5_000)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");

        var normalizedProcessName = string.IsNullOrWhiteSpace(processName) ? null : processName;
        if (!processId.HasValue && normalizedProcessName is null)
            return Error("INVALID_REQUEST", "At least one of processId or processName is required.");
        if (processId is <= 0)
            return Error("INVALID_REQUEST", "processId must be greater than zero.");
        if (normalizedProcessName is not null
            && (normalizedProcessName.Length > 128 || normalizedProcessName.Any(char.IsControl)))
        {
            return Error("INVALID_REQUEST", "processName must be at most 128 characters without control characters.");
        }
        if (timeoutMs is < 1 or > 300_000)
            return Error("INVALID_REQUEST", "timeoutMs must be between 1 and 300000.");

        var command = new AppCloseCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessId = processId,
            ProcessName = normalizedProcessName,
            AllMatches = allMatches,
            Force = force,
            EntireProcessTree = entireProcessTree,
            TimeoutMs = timeoutMs
        };

        try
        {
            var result = await _dispatcher.SendAsync<AppCloseResult>(command, RequestToken());
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

            return Error(
                result.Error?.Code ?? "INTERNAL_ERROR",
                result.Error?.Message ?? "Command execution failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected app_close failure for device {DeviceId}", deviceId);
            return Error("INTERNAL_ERROR", "An unexpected gateway error occurred.");
        }
    }

    private async Task<bool> AuthorizedAsync()
    {
        var principal = _httpContextAccessor?.HttpContext?.User
            ?? new ClaimsPrincipal(new ClaimsIdentity());
        return (await _authorizationService.AuthorizeAsync(principal, null, "DevExecutePolicy")).Succeeded;
    }

    private CancellationToken RequestToken() =>
        _httpContextAccessor?.HttpContext?.RequestAborted ?? CancellationToken.None;

    private static CallToolResult Error(string code, string message) =>
        new()
        {
            Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
            IsError = true
        };
}
