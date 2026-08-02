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
public sealed class UiGridActionTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<UiGridActionTools> _logger;

    public UiGridActionTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<UiGridActionTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "ui_grid_select",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false),
     Description("Selects, adds, removes, or activates one Windows UI Automation grid item by zero-based row and column. Realizes virtualized items, scrolls them into view when supported, and verifies selection state changes. Requires dev:execute scope.")]
    public async Task<CallToolResult> SelectAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("Action: select, add, remove, or activate (default: select)")] string action = UiGridSelectActions.Select,
        [Description("Optional exact automationId selector for the grid")] string? automationId = null,
        [Description("Optional exact control name selector for the grid")] string? name = null,
        [Description("Optional exact grid control type such as DataGrid or Table; at least one selector is required")] string? controlType = null,
        [Description("Zero-based index when multiple grids match (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Zero-based row index inside the grid")] int row = 0,
        [Description("Zero-based column index inside the grid")] int column = 0,
        [Description("Whether to focus the target window and grid item before acting (default: true)")] bool focusWindow = true)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (!ValidText(windowHandle, 32))
            return Error("INVALID_REQUEST", "windowHandle is invalid.");
        if (!UiGridSelectActions.TryNormalize(action, out var normalizedAction))
            return Error("INVALID_REQUEST", "action must be one of: select, add, remove, activate.");
        if (string.IsNullOrWhiteSpace(automationId)
            && string.IsNullOrWhiteSpace(name)
            && string.IsNullOrWhiteSpace(controlType))
            return Error("INVALID_REQUEST", "automationId, name, or controlType is required.");
        if (!OptionalText(automationId, 1024)
            || !OptionalText(name, 1024)
            || !OptionalText(controlType, 128))
            return Error("INVALID_REQUEST", "Selector values are invalid.");
        if (occurrenceIndex is < 0 or > 1000)
            return Error("INVALID_REQUEST", "occurrenceIndex must be between 0 and 1000.");
        if (row is < 0 or > 1_000_000)
            return Error("INVALID_REQUEST", "row must be between 0 and 1000000.");
        if (column is < 0 or > 100_000)
            return Error("INVALID_REQUEST", "column must be between 0 and 100000.");

        var command = new UiGridSelectCommand
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
            Row = row,
            Column = column,
            FocusWindow = focusWindow
        };

        try
        {
            var result = await _dispatcher.SendAsync<UiGridSelectResult>(command, RequestToken());
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
            _logger.LogError(ex, "Unexpected ui_grid_select failure for device {DeviceId}", deviceId);
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

