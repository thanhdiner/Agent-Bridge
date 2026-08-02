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
public sealed class UiExpandCollapseTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<UiExpandCollapseTools> _logger;

    public UiExpandCollapseTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<UiExpandCollapseTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "ui_expand_collapse",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Expands, collapses, or toggles one Windows UI Automation control through ExpandCollapsePattern, scrolls it into view when supported, and verifies the resulting state. Requires dev:execute scope.")]
    public async Task<CallToolResult> ExpandCollapseAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("Action: expand, collapse, or toggle (default: toggle)")] string action = UiExpandCollapseActions.Toggle,
        [Description("Exact automationId; either automationId or name is required")] string? automationId = null,
        [Description("Exact control name; either automationId or name is required")] string? name = null,
        [Description("Optional exact control type such as ComboBox, TreeItem, MenuItem, or Button")] string? controlType = null,
        [Description("Zero-based index when multiple controls match (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Whether to focus the target window before changing state (default: true)")] bool focusWindow = true)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (!ValidText(windowHandle, 32))
            return Error("INVALID_REQUEST", "windowHandle is invalid.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            return Error("INVALID_REQUEST", "automationId or name is required.");
        if (!OptionalText(automationId, 1024) || !OptionalText(name, 1024) || !OptionalText(controlType, 128))
            return Error("INVALID_REQUEST", "Selector values are invalid.");
        if (occurrenceIndex is < 0 or > 1000)
            return Error("INVALID_REQUEST", "occurrenceIndex must be between 0 and 1000.");
        if (!UiExpandCollapseActions.TryNormalize(action, out var normalizedAction))
            return Error("INVALID_REQUEST", "action must be one of: expand, collapse, toggle.");

        var command = new UiExpandCollapseCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = windowHandle,
            Action = normalizedAction,
            AutomationId = automationId,
            Name = name,
            ControlType = controlType,
            OccurrenceIndex = occurrenceIndex,
            FocusWindow = focusWindow
        };

        try
        {
            var result = await _dispatcher.SendAsync<UiExpandCollapseResult>(command, RequestToken());
            if (result.Success && result.Data is not null)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result.Data, JsonOptions.Default) }],
                    IsError = false
                };
            }

            return Error(
                result.Error?.Code ?? "INTERNAL_ERROR",
                result.Error?.Message ?? "Command execution failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected ui_expand_collapse failure for device {DeviceId}", deviceId);
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

    private static bool ValidText(string? value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);

    private static bool OptionalText(string? value, int limit) =>
        value is null || (value.Length <= limit && !value.Any(char.IsControl));

    private static CallToolResult Error(string code, string message) => new()
    {
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
        IsError = true
    };
}

