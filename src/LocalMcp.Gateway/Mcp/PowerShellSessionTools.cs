using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Gateway.Commands;

namespace LocalMcp.Gateway.Mcp;

[McpServerToolType]
public sealed class PowerShellSessionTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<PowerShellSessionTools> _logger;

    public PowerShellSessionTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<PowerShellSessionTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    // ── powershell_start ──────────────────────────────────────────────────────

    [McpServerTool(Name = "powershell_start", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description(
         "Starts an async PowerShell 7 (pwsh.exe) session on a target Windows agent device and returns immediately " +
         "with a sessionId. The script runs in the background; use powershell_status to poll for output and " +
         "powershell_cancel to cancel. " +
         "Ask the user for confirmation before executing potentially destructive scripts. " +
         "Requires dev:execute scope. " +
         "The script is not filesystem-sandboxed and runs with the agent user's privileges.")]
    public async Task<CallToolResult> StartSessionAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The absolute working directory path on the agent (must be within AllowedRoots)")] string workingDirectory,
        [Description("The PowerShell 7 script to run asynchronously")] string script,
        [Description("Timeout in seconds before the agent kills the script (1–900, default: 120)")] int timeoutSeconds = 120,
        [Description("Maximum total combined bytes of stdout and stderr to retain in memory (1024–4194304, default: 1048576)")] int maxOutputBytes = 1_048_576)
    {
        if (!await AuthorizeScopeAsync("DevExecutePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: dev:execute");

        if (string.IsNullOrWhiteSpace(workingDirectory))
            return CreateErrorResult("INVALID_REQUEST", "workingDirectory parameter is required.");

        if (string.IsNullOrWhiteSpace(script) || script.Length > 65_536 || script.Contains('\0'))
            return CreateErrorResult("INVALID_REQUEST", "script must be non-empty, contain no NUL characters, and be at most 65536 characters.");

        if (timeoutSeconds is < 1 or > 900)
            return CreateErrorResult("INVALID_REQUEST", "timeoutSeconds must be between 1 and 900.");

        if (maxOutputBytes is < 1024 or > 4_194_304)
            return CreateErrorResult("INVALID_REQUEST", "maxOutputBytes must be between 1024 and 4194304.");

        var command = new PowerShellStartCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkingDirectory = workingDirectory,
            Script = script,
            TimeoutSeconds = timeoutSeconds,
            MaxOutputBytes = maxOutputBytes
        };

        return await DispatchAsync<PowerShellStartResult>(command, "powershell_start", deviceId, GetCancellationToken());
    }

    // ── powershell_status ─────────────────────────────────────────────────────

    [McpServerTool(Name = "powershell_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description(
         "Polls the status and incremental output of a running or completed PowerShell session on a target " +
         "Windows agent device. Pass nextStdoutOffset and nextStderrOffset from the previous response as stdoutOffset and stderrOffset to page through output. " +
         "Requires dev:execute scope.")]
    public async Task<CallToolResult> GetSessionStatusAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The sessionId returned by powershell_start")] string sessionId,
        [Description("Byte offset into the stdout buffer; use 0 on first call, then nextStdoutOffset from each response")] long stdoutOffset = 0,
        [Description("Byte offset into the stderr buffer; use 0 on first call, then nextStderrOffset from each response")] long stderrOffset = 0,
        [Description("Maximum bytes of output to return per call (1–262144, default: 65536)")] int maxOutputBytes = 65_536)
    {
        if (!await AuthorizeScopeAsync("DevExecutePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: dev:execute");

        if (!Guid.TryParse(sessionId, out var parsedSessionId))
            return CreateErrorResult("INVALID_REQUEST", "sessionId must be a valid GUID.");

        if (stdoutOffset < 0 || stderrOffset < 0)
            return CreateErrorResult("INVALID_REQUEST", "stdoutOffset and stderrOffset must be >= 0.");

        if (maxOutputBytes is < 4 or > 262_144)
            return CreateErrorResult("INVALID_REQUEST", "maxOutputBytes must be between 4 and 262144.");

        var command = new PowerShellStatusCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = parsedSessionId,
            StdoutOffset = stdoutOffset,
            StderrOffset = stderrOffset,
            MaxOutputBytes = maxOutputBytes
        };

        return await DispatchAsync<PowerShellSessionResult>(command, "powershell_status", deviceId, GetCancellationToken());
    }

    // ── powershell_cancel ─────────────────────────────────────────────────────

    [McpServerTool(Name = "powershell_cancel", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description(
         "Cancels a running PowerShell session on a target Windows agent device, killing the process tree. " +
         "Idempotent: safe to call on sessions that have already completed. Returns the session's final state. " +
         "Requires dev:execute scope.")]
    public async Task<CallToolResult> CancelSessionAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The sessionId returned by powershell_start")] string sessionId)
    {
        if (!await AuthorizeScopeAsync("DevExecutePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: dev:execute");

        if (!Guid.TryParse(sessionId, out var parsedSessionId))
            return CreateErrorResult("INVALID_REQUEST", "sessionId must be a valid GUID.");

        var command = new PowerShellCancelCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = parsedSessionId
        };

        return await DispatchAsync<PowerShellSessionResult>(command, "powershell_cancel", deviceId, GetCancellationToken());
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<bool> AuthorizeScopeAsync(string policyName)
    {
        var principal = _httpContextAccessor?.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        var authResult = await _authorizationService.AuthorizeAsync(principal, null, policyName);
        return authResult.Succeeded;
    }

    private CancellationToken GetCancellationToken()
        => _httpContextAccessor?.HttpContext?.RequestAborted ?? CancellationToken.None;

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
            return CreateErrorResult(errorCode, errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing {ToolName} for device {DeviceId}", toolName, deviceId);
            return CreateErrorResult("INTERNAL_ERROR", "An unexpected error occurred on the gateway.");
        }
    }

    private static CallToolResult CreateErrorResult(string code, string message) =>
        new()
        {
            Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
            IsError = true
        };
}


