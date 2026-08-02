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
public sealed class ProcessWaitTools
{
    private const int MaxProcessNameCharacters = 260;
    private const int MaxTimeoutMs = 300_000;

    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<ProcessWaitTools> _logger;

    public ProcessWaitTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<ProcessWaitTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "process_wait",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Waits until a Windows process appears or disappears. Select by PID, process name, or both. Requires dev:execute scope.")]
    public async Task<CallToolResult> WaitProcessAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Optional exact process ID greater than zero")] int? processId = null,
        [Description("Optional exact process name, case-insensitive; .exe suffix is ignored")] string? processName = null,
        [Description("Zero-based index when multiple live processes match processName (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Condition: exists or not-exists. Aliases: appears, disappears, exited (default: exists)")] string condition = ProcessWaitConditions.Exists,
        [Description("Maximum wait in milliseconds (default: 10000, hard limit: 300000)")] int timeoutMs = 10_000,
        [Description("Delay between polls in milliseconds (default: 200, range: 25-5000)")] int pollIntervalMs = 200)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (!processId.HasValue && string.IsNullOrWhiteSpace(processName))
            return Error("INVALID_REQUEST", "processId or processName is required.");
        if (processId is <= 0)
            return Error("INVALID_REQUEST", "processId must be greater than zero.");
        if (processName is not null
            && (string.IsNullOrWhiteSpace(processName)
                || processName.Length > MaxProcessNameCharacters
                || processName.Any(char.IsControl)))
        {
            return Error("INVALID_REQUEST", "processName is invalid.");
        }
        if (occurrenceIndex is < 0 or > 1000)
            return Error("INVALID_REQUEST", "occurrenceIndex must be between 0 and 1000.");
        if (!ProcessWaitConditions.TryNormalize(condition, out var normalizedCondition))
            return Error("INVALID_REQUEST", "condition must be exists or not-exists. Aliases: appears, disappears, exited.");
        if (timeoutMs is < 1 or > MaxTimeoutMs)
            return Error("INVALID_REQUEST", $"timeoutMs must be between 1 and {MaxTimeoutMs}.");
        if (pollIntervalMs is < 25 or > 5000)
            return Error("INVALID_REQUEST", "pollIntervalMs must be between 25 and 5000.");

        var command = new ProcessWaitCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessId = processId,
            ProcessName = processName,
            OccurrenceIndex = occurrenceIndex,
            Condition = normalizedCondition,
            TimeoutMs = timeoutMs,
            PollIntervalMs = pollIntervalMs
        };

        try
        {
            var result = await _dispatcher.SendAsync<ProcessWaitResult>(command, RequestToken());
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
            _logger.LogError(ex, "Unexpected process_wait failure for device {DeviceId}", deviceId);
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

    private static CallToolResult Error(string code, string message) => new()
    {
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
        IsError = true
    };
}

