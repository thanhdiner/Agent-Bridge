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
public sealed class UiAutomationTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<UiAutomationTools> _logger;

    public UiAutomationTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<UiAutomationTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "window_list",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Lists bounded top-level Windows windows and returns title, process name, PID, native handle, class name, bounds, visibility, enabled, minimized, maximized, foreground, and cloaked states. Foreground window is returned first. Requires dev:execute scope.")]
    public async Task<CallToolResult> ListWindowsAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Whether to include invisible or cloaked top-level windows (default: false)")] bool includeInvisible = false,
        [Description("Whether to include windows with an empty title (default: false)")] bool includeUntitled = false,
        [Description("Maximum windows returned (default: 100, hard limit: 500)")] int maxWindows = 100)
    {
        if (!await AuthorizeScopeAsync())
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (maxWindows is < 1 or > 500)
            return CreateErrorResult("INVALID_REQUEST", "maxWindows must be between 1 and 500.");

        var command = new WindowListCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            IncludeInvisible = includeInvisible,
            IncludeUntitled = includeUntitled,
            MaxWindows = maxWindows
        };

        try
        {
            var result = await _dispatcher.SendAsync<WindowListResult>(command, GetCancellationToken());
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
            _logger.LogError(ex, "Unexpected error executing window_list for device {DeviceId}", deviceId);
            return CreateErrorResult("INTERNAL_ERROR", "An unexpected error occurred on the gateway.");
        }
    }

    [McpServerTool(
        Name = "ui_find",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Searches a bounded Windows UI Automation tree for matching controls. automationId and controlType are exact case-insensitive matches; nameContains is a case-insensitive substring. Returns compact control metadata, supported patterns, and occurrenceIndex for follow-up UI actions. Requires dev:execute scope.")]
    public async Task<CallToolResult> FindUiAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("Optional exact automationId match")] string? automationId = null,
        [Description("Optional case-insensitive substring of the control name")] string? nameContains = null,
        [Description("Optional exact control type such as Button, Edit, Document, or Pane")] string? controlType = null,
        [Description("Maximum descendant depth from the window root (default: 8, hard limit: 20)")] int maxDepth = 8,
        [Description("Maximum matching controls returned (default: 50, hard limit: 100)")] int maxResults = 50)
    {
        if (!await AuthorizeScopeAsync())
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(windowHandle) || windowHandle.Length > 32 || windowHandle.Any(char.IsControl))
            return CreateErrorResult("INVALID_REQUEST", "windowHandle is required and must be at most 32 characters without control characters.");
        if (string.IsNullOrWhiteSpace(automationId)
            && string.IsNullOrWhiteSpace(nameContains)
            && string.IsNullOrWhiteSpace(controlType))
            return CreateErrorResult("INVALID_REQUEST", "automationId, nameContains, or controlType is required.");
        if (automationId?.Length > 1024 || nameContains?.Length > 1024 || controlType?.Length > 128)
            return CreateErrorResult("INVALID_REQUEST", "automationId and nameContains must be at most 1024 characters; controlType must be at most 128 characters.");
        if (maxDepth is < 0 or > 20)
            return CreateErrorResult("INVALID_REQUEST", "maxDepth must be between 0 and 20.");
        if (maxResults is < 1 or > 100)
            return CreateErrorResult("INVALID_REQUEST", "maxResults must be between 1 and 100.");

        var command = new UiFindCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = windowHandle,
            AutomationId = automationId,
            NameContains = nameContains,
            ControlType = controlType,
            MaxDepth = maxDepth,
            MaxResults = maxResults
        };

        try
        {
            var result = await _dispatcher.SendAsync<UiFindResult>(command, GetCancellationToken());
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
            _logger.LogError(ex, "Unexpected error executing ui_find for device {DeviceId}", deviceId);
            return CreateErrorResult("INTERNAL_ERROR", "An unexpected error occurred on the gateway.");
        }
    }

    [McpServerTool(
        Name = "ui_tree",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Reads a bounded Windows UI Automation control tree for one live window handle. Returns each control's name, automationId, controlType, bounds, enabled state, and a bounded value when supported. Password values are always redacted. Requires dev:execute scope.")]
    public async Task<CallToolResult> GetUiTreeAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("Maximum descendant depth from the window root (default: 6, hard limit: 20)")] int maxDepth = 6,
        [Description("Maximum total controls returned including the root (default: 500, hard limit: 1000)")] int maxNodes = 500)
    {
        if (!await AuthorizeScopeAsync())
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(windowHandle) || windowHandle.Length > 32 || windowHandle.Any(char.IsControl))
            return CreateErrorResult("INVALID_REQUEST", "windowHandle is required and must be at most 32 characters without control characters.");
        if (maxDepth is < 0 or > 20)
            return CreateErrorResult("INVALID_REQUEST", "maxDepth must be between 0 and 20.");
        if (maxNodes is < 1 or > 1000)
            return CreateErrorResult("INVALID_REQUEST", "maxNodes must be between 1 and 1000.");

        var command = new UiTreeCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = windowHandle,
            MaxDepth = maxDepth,
            MaxNodes = maxNodes
        };

        try
        {
            var result = await _dispatcher.SendAsync<UiTreeResult>(command, GetCancellationToken());
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
            _logger.LogError(ex, "Unexpected error executing ui_tree for device {DeviceId}", deviceId);
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
            "DevExecutePolicy");
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

