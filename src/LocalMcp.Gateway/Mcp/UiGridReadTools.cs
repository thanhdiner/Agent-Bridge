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
public sealed class UiGridReadTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<UiGridReadTools> _logger;

    public UiGridReadTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<UiGridReadTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "ui_grid_read",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Reads a bounded row and column window from one Windows UI Automation GridPattern or TablePattern control, including headers and cell metadata. Password values are always redacted. Requires dev:execute scope.")]
    public async Task<CallToolResult> ReadAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("Optional exact automationId selector")] string? automationId = null,
        [Description("Optional exact control name selector")] string? name = null,
        [Description("Optional exact control type such as DataGrid or Table; at least one selector is required")] string? controlType = null,
        [Description("Zero-based index when multiple controls match (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Zero-based first row to return (default: 0)")] int rowStart = 0,
        [Description("Maximum rows requested (default: 50, hard limit: 1000)")] int rowCount = 50,
        [Description("Zero-based first column to return (default: 0)")] int columnStart = 0,
        [Description("Maximum columns requested (default: 20, hard limit: 1000)")] int columnCount = 20,
        [Description("Maximum total cells allowed for this request (default and hard limit: 1000)")] int maxCells = 1000,
        [Description("Whether to focus the target window before reading (default: false)")] bool focusWindow = false)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(deviceId))
            return Error("INVALID_REQUEST", "deviceId parameter is required.");
        if (!ValidText(windowHandle, 32))
            return Error("INVALID_REQUEST", "windowHandle is invalid.");
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
        if (rowStart is < 0 or > 1_000_000)
            return Error("INVALID_REQUEST", "rowStart must be between 0 and 1000000.");
        if (rowCount is < 1 or > 1000)
            return Error("INVALID_REQUEST", "rowCount must be between 1 and 1000.");
        if (columnStart is < 0 or > 100_000)
            return Error("INVALID_REQUEST", "columnStart must be between 0 and 100000.");
        if (columnCount is < 1 or > 1000)
            return Error("INVALID_REQUEST", "columnCount must be between 1 and 1000.");
        if (maxCells is < 1 or > 1000)
            return Error("INVALID_REQUEST", "maxCells must be between 1 and 1000.");
        if ((long)rowCount * columnCount > maxCells)
            return Error("UI_GRID_LIMIT_EXCEEDED", "rowCount multiplied by columnCount exceeds maxCells.");

        var command = new UiGridReadCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = windowHandle,
            AutomationId = automationId,
            Name = name,
            ControlType = controlType,
            OccurrenceIndex = occurrenceIndex,
            RowStart = rowStart,
            RowCount = rowCount,
            ColumnStart = columnStart,
            ColumnCount = columnCount,
            MaxCells = maxCells,
            FocusWindow = focusWindow
        };

        try
        {
            var result = await _dispatcher.SendAsync<UiGridReadResult>(command, RequestToken());
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
            _logger.LogError(ex, "Unexpected ui_grid_read failure for device {DeviceId}", deviceId);
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
