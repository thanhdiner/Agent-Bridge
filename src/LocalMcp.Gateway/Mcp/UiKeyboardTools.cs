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
public sealed class UiKeyboardTools
{
    private const int MaxKeyChordCharacters = 64;
    private const int MaxTypedTextCharacters = 4096;
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<UiKeyboardTools> _logger;

    public UiKeyboardTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<UiKeyboardTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "ui_press_key",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false),
     Description("Performs a standard keyboard action (such as key gesture or shortcut) on a targeted user interface control. Requires dev:execute scope.")]
    public async Task<CallToolResult> PressKeyAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("The key gesture to execute, represented by + joined tokens (e.g. CTRL+L, F5). Maximum 64 characters.")] string keys,
        [Description("Optional exact automationId of the control to target")] string? automationId = null,
        [Description("Optional exact control name of the control to target")] string? name = null,
        [Description("Optional exact control type such as Edit or Button")] string? controlType = null,
        [Description("Zero-based index when multiple controls match (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Whether to focus the target window before executing the key gesture (default: true)")] bool focusWindow = true)
    {
        var validation = await ValidateSelectorAsync(
            deviceId,
            windowHandle,
            automationId,
            name,
            controlType,
            occurrenceIndex,
            requireSelector: false);
        if (validation is not null)
            return validation;

        if (string.IsNullOrWhiteSpace(keys))
            return Error("INVALID_REQUEST", "keys parameter is required.");
        if (keys.Length > MaxKeyChordCharacters)
            return Error("INVALID_REQUEST", $"keys must be at most {MaxKeyChordCharacters} characters.");

        return await DispatchAsync<UiPressKeyResult>(
            new UiPressKeyCommand
            {
                CommandId = Guid.NewGuid(),
                DeviceId = deviceId,
                CreatedAt = DateTimeOffset.UtcNow,
                WindowHandle = windowHandle,
                Keys = keys,
                AutomationId = automationId,
                Name = name,
                ControlType = controlType,
                OccurrenceIndex = occurrenceIndex,
                FocusWindow = focusWindow
            },
            "ui_press_key");
    }

    [McpServerTool(
        Name = "ui_type_text",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false),
     Description("Enters Unicode text characters into a targeted user interface text input control. Requires dev:execute scope.")]
    public async Task<CallToolResult> TypeTextAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("The Unicode text characters to enter. Maximum 4096 characters. The typed text is not returned in the result.")] string text,
        [Description("Exact automationId; either automationId or name is required")] string? automationId = null,
        [Description("Exact control name; either automationId or name is required")] string? name = null,
        [Description("Optional exact control type such as Edit or Document")] string? controlType = null,
        [Description("Zero-based index when multiple controls match (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Whether to focus the target window and matched control before entering text (default: true)")] bool focusWindow = true)
    {
        var validation = await ValidateSelectorAsync(
            deviceId,
            windowHandle,
            automationId,
            name,
            controlType,
            occurrenceIndex,
            requireSelector: true);
        if (validation is not null)
            return validation;

        if (string.IsNullOrEmpty(text))
            return Error("INVALID_REQUEST", "text parameter is required.");
        if (text.Length > MaxTypedTextCharacters)
            return Error("INVALID_REQUEST", $"text must be at most {MaxTypedTextCharacters} characters.");

        return await DispatchAsync<UiTypeTextResult>(
            new UiTypeTextCommand
            {
                CommandId = Guid.NewGuid(),
                DeviceId = deviceId,
                CreatedAt = DateTimeOffset.UtcNow,
                WindowHandle = windowHandle,
                Text = text,
                AutomationId = automationId,
                Name = name,
                ControlType = controlType,
                OccurrenceIndex = occurrenceIndex,
                FocusWindow = focusWindow
            },
            "ui_type_text");
    }

    private async Task<CallToolResult?> ValidateSelectorAsync(
        string deviceId,
        string windowHandle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool requireSelector)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(deviceId))
            return Error("INVALID_REQUEST", "deviceId parameter is required.");
        if (!ValidText(windowHandle, 32))
            return Error("INVALID_REQUEST", "windowHandle is invalid.");
        if (requireSelector && string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
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
