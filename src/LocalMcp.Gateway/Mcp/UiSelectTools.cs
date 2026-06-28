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
public sealed class UiSelectTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<UiSelectTools> _logger;

    public UiSelectTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<UiSelectTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "ui_select",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Selects, adds, or removes one Windows UI Automation selection item, scrolls it into view when supported, and verifies the resulting selected state. Requires dev:execute scope.")]
    public async Task<CallToolResult> SelectAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("Selection action: select, add, or remove (default: select)")] string action = UiSelectActions.Select,
        [Description("Exact automationId; either automationId or name is required")] string? automationId = null,
        [Description("Exact control name; either automationId or name is required")] string? name = null,
        [Description("Optional exact control type such as ListItem, TabItem, TreeItem, or DataItem")] string? controlType = null,
        [Description("Zero-based index when multiple controls match (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Whether to focus the target window before changing selection (default: true)")] bool focusWindow = true)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(deviceId))
            return Error("INVALID_REQUEST", "deviceId parameter is required.");
        if (!ValidText(windowHandle, 32))
            return Error("INVALID_REQUEST", "windowHandle is invalid.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            return Error("INVALID_REQUEST", "automationId or name is required.");
        if (!OptionalText(automationId, 1024) || !OptionalText(name, 1024) || !OptionalText(controlType, 128))
            return Error("INVALID_REQUEST", "Selector values are invalid.");
        if (occurrenceIndex is < 0 or > 1000)
            return Error("INVALID_REQUEST", "occurrenceIndex must be between 0 and 1000.");
        if (!UiSelectActions.TryNormalize(action, out var normalizedAction))
            return Error("INVALID_REQUEST", "action must be one of: select, add, remove.");

        var command = new UiSelectCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
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
            var result = await _dispatcher.SendAsync<UiSelectResult>(command, RequestToken());
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
            _logger.LogError(ex, "Unexpected ui_select failure for device {DeviceId}", deviceId);
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
