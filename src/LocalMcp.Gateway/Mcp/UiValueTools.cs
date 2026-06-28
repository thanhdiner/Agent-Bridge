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
public sealed class UiValueTools
{
    private const int MaxValueCharacters = 65_536;
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<UiValueTools> _logger;

    public UiValueTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<UiValueTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "ui_get_value",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Reads one Windows UI Automation control value selected by automationId or exact name. Password values are always redacted. Requires dev:execute scope.")]
    public async Task<CallToolResult> GetValueAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("Exact automationId; either automationId or name is required")] string? automationId = null,
        [Description("Exact control name; either automationId or name is required")] string? name = null,
        [Description("Optional exact control type such as Edit, Document, Spinner, or Slider")] string? controlType = null,
        [Description("Zero-based index when multiple controls match (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Whether to focus the target window before reading (default: false)")] bool focusWindow = false)
    {
        var validation = await ValidateSelectorAsync(
            deviceId,
            windowHandle,
            automationId,
            name,
            controlType,
            occurrenceIndex);
        if (validation is not null)
            return validation;

        return await DispatchAsync<UiGetValueResult>(
            new UiGetValueCommand
            {
                CommandId = Guid.NewGuid(),
                DeviceId = deviceId,
                CreatedAt = DateTimeOffset.UtcNow,
                WindowHandle = windowHandle,
                AutomationId = automationId,
                Name = name,
                ControlType = controlType,
                OccurrenceIndex = occurrenceIndex,
                FocusWindow = focusWindow
            },
            "ui_get_value");
    }

    [McpServerTool(
        Name = "ui_set_value",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false),
     Description("Writes one Windows UI Automation control value selected by automationId or exact name. Empty value clears the control; append adds to its current readable value. Password values are never returned. Requires dev:execute scope.")]
    public async Task<CallToolResult> SetValueAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("The value to write. Use an empty string to clear the control. Maximum 65536 characters")] string value,
        [Description("Exact automationId; either automationId or name is required")] string? automationId = null,
        [Description("Exact control name; either automationId or name is required")] string? name = null,
        [Description("Optional exact control type such as Edit or Document")] string? controlType = null,
        [Description("Zero-based index when multiple controls match (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Whether to focus the target window and matched control before writing (default: true)")] bool focusWindow = true,
        [Description("Whether to append to the current readable value instead of replacing it (default: false)")] bool append = false)
    {
        var validation = await ValidateSelectorAsync(
            deviceId,
            windowHandle,
            automationId,
            name,
            controlType,
            occurrenceIndex);
        if (validation is not null)
            return validation;
        if (value is null)
            return Error("INVALID_REQUEST", "value is required. Use an empty string to clear the control.");
        if (value.Length > MaxValueCharacters)
            return Error("INVALID_REQUEST", $"value must be at most {MaxValueCharacters} characters.");

        return await DispatchAsync<UiSetValueResult>(
            new UiSetValueCommand
            {
                CommandId = Guid.NewGuid(),
                DeviceId = deviceId,
                CreatedAt = DateTimeOffset.UtcNow,
                WindowHandle = windowHandle,
                Value = value,
                AutomationId = automationId,
                Name = name,
                ControlType = controlType,
                OccurrenceIndex = occurrenceIndex,
                FocusWindow = focusWindow,
                Append = append
            },
            "ui_set_value");
    }

    private async Task<CallToolResult?> ValidateSelectorAsync(
        string deviceId,
        string windowHandle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex)
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
            return Error("INVALID_REQUEST", "Selector values exceed their limits or contain control characters.");
        if (occurrenceIndex is < 0 or > 1000)
            return Error("INVALID_REQUEST", "occurrenceIndex must be between 0 and 1000.");
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
            _logger.LogError(ex, "Unexpected {ToolName} failure for device {DeviceId}", toolName, command.DeviceId);
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

    private static CallToolResult Error(string code, string message) =>
        new()
        {
            Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
            IsError = true
        };
}
