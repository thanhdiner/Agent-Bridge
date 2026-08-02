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
public sealed class UiTextReadTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<UiTextReadTools> _logger;

    public UiTextReadTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<UiTextReadTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "ui_get_text",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Reads bounded text from one Windows UI Automation control. This is the Phase 4 alias of ui_text_read and supports document, visible, or selection scopes. Password text is always redacted. Requires dev:execute scope.")]
    public Task<CallToolResult> GetTextAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("Text scope: document, visible, or selection (default: document)")] string scope = UiTextReadScopes.Document,
        [Description("Optional exact automationId selector")] string? automationId = null,
        [Description("Optional exact control name selector")] string? name = null,
        [Description("Optional exact control type such as Document, Edit, or Text; at least one selector is required")] string? controlType = null,
        [Description("Zero-based index when multiple controls match (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Zero-based first line to return (default: 0)")] int startLine = 0,
        [Description("Maximum lines requested (default: 200, hard limit: 10000)")] int lineCount = 200,
        [Description("Maximum characters returned (default and hard limit: 65536)")] int maxCharacters = 65_536,
        [Description("Whether to focus the target window before reading (default: false)")] bool focusWindow = false) =>
        ReadAsync(
            deviceId,
            windowHandle,
            scope,
            automationId,
            name,
            controlType,
            occurrenceIndex,
            startLine,
            lineCount,
            maxCharacters,
            focusWindow);

    [McpServerTool(
        Name = "ui_text_read",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Reads a bounded document, visible text, or selected text range from one Windows UI Automation control. Supports line paging, TextPattern/TextPattern2, and safe Value or Legacy fallback for document scope. Password text is always redacted. Requires dev:execute scope.")]
    public async Task<CallToolResult> ReadAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("Text scope: document, visible, or selection (default: document)")] string scope = UiTextReadScopes.Document,
        [Description("Optional exact automationId selector")] string? automationId = null,
        [Description("Optional exact control name selector")] string? name = null,
        [Description("Optional exact control type such as Document, Edit, or Text; at least one selector is required")] string? controlType = null,
        [Description("Zero-based index when multiple controls match (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Zero-based first line to return (default: 0)")] int startLine = 0,
        [Description("Maximum lines requested (default: 200, hard limit: 10000)")] int lineCount = 200,
        [Description("Maximum characters returned (default and hard limit: 65536)")] int maxCharacters = 65_536,
        [Description("Whether to focus the target window before reading (default: false)")] bool focusWindow = false)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (!ValidText(windowHandle, 32))
            return Error("INVALID_REQUEST", "windowHandle is invalid.");
        if (!UiTextReadScopes.TryNormalize(scope, out var normalizedScope))
            return Error("INVALID_REQUEST", "scope must be one of: document, visible, selection.");
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
        if (startLine is < 0 or > 1_000_000)
            return Error("INVALID_REQUEST", "startLine must be between 0 and 1000000.");
        if (lineCount is < 1 or > 10_000)
            return Error("INVALID_REQUEST", "lineCount must be between 1 and 10000.");
        if (maxCharacters is < 1 or > 65_536)
            return Error("UI_TEXT_LIMIT_EXCEEDED", "maxCharacters must be between 1 and 65536.");

        var command = new UiTextReadCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = windowHandle,
            Scope = normalizedScope,
            AutomationId = automationId,
            Name = name,
            ControlType = controlType,
            OccurrenceIndex = occurrenceIndex,
            StartLine = startLine,
            LineCount = lineCount,
            MaxCharacters = maxCharacters,
            FocusWindow = focusWindow
        };

        try
        {
            var result = await _dispatcher.SendAsync<UiTextReadResult>(command, RequestToken());
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
            _logger.LogError(ex, "Unexpected ui_text_read failure for device {DeviceId}", deviceId);
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

