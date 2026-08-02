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
public sealed class UiScrollTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<UiScrollTools> _logger;
    public UiScrollTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<UiScrollTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }
    [McpServerTool(
        Name = "ui_scroll",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false),
     Description("Scrolls one targeted Windows UI Automation control through ScrollPattern, then ScrollItemPattern for nested vertical containers, with a verified keyboard fallback. Does not emulate a mouse wheel. Requires dev:execute scope.")]
    public async Task<CallToolResult> ScrollAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("Direction: up, down, left, or right")] string direction,
        [Description("Scroll amount: small, page, or end (default: page)")] string amount = UiScrollAmounts.Page,
        [Description("Optional exact automationId selector")] string? automationId = null,
        [Description("Optional exact control name selector")] string? name = null,
        [Description("Optional exact control type such as Document, Pane, List, or Tree; may be used by itself")] string? controlType = null,
        [Description("Zero-based index when multiple controls match (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Whether to focus the target window before scrolling (default: true)")] bool focusWindow = true)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (!ValidText(windowHandle, 32))
            return Error("INVALID_REQUEST", "windowHandle is invalid.");
        if (string.IsNullOrWhiteSpace(automationId)
            && string.IsNullOrWhiteSpace(name)
            && string.IsNullOrWhiteSpace(controlType))
            return Error("INVALID_REQUEST", "automationId, name, or controlType is required.");
        if (!OptionalText(automationId, 1024) || !OptionalText(name, 1024) || !OptionalText(controlType, 128))
            return Error("INVALID_REQUEST", "Selector values exceed their limits or contain control characters.");
        if (occurrenceIndex is < 0 or > 1000)
            return Error("INVALID_REQUEST", "occurrenceIndex must be between 0 and 1000.");
        if (!UiScrollDirections.TryNormalize(direction, out var normalizedDirection))
            return Error("INVALID_REQUEST", "direction must be one of: up, down, left, right.");
        if (!UiScrollAmounts.TryNormalize(amount, out var normalizedAmount))
            return Error("INVALID_REQUEST", "amount must be one of: small, page, end.");
        var command = new UiScrollCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = windowHandle,
            Direction = normalizedDirection,
            Amount = normalizedAmount,
            AutomationId = automationId,
            Name = name,
            ControlType = controlType,
            OccurrenceIndex = occurrenceIndex,
            FocusWindow = focusWindow
        };
        try
        {
            var result = await _dispatcher.SendAsync<UiScrollResult>(command, RequestToken());
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
            _logger.LogError(ex, "Unexpected ui_scroll failure for device {DeviceId}", deviceId);
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
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= limit
        && !value.Any(char.IsControl);
    private static bool OptionalText(string? value, int limit) =>
        value is null || (value.Length <= limit && !value.Any(char.IsControl));
    private static CallToolResult Error(string code, string message) => new()
    {
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
        IsError = true
    };
}

