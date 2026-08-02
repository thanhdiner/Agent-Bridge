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
public sealed class UiWaitTools
{
    private const int MaxExpectedValueCharacters = 65_536;
    private const int MaxTimeoutMs = 300_000;
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<UiWaitTools> _logger;

    public UiWaitTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<UiWaitTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "ui_wait",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Waits until a Windows UI Automation control appears, disappears, becomes enabled, disabled, focused, matches a value, or changes value. Does not focus or modify the window. Requires dev:execute scope.")]
    public async Task<CallToolResult> WaitUiAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("Exact automationId; either automationId or name is required")] string? automationId = null,
        [Description("Exact control name; either automationId or name is required")] string? name = null,
        [Description("Optional exact control type such as Button, Edit, Document, or Pane")] string? controlType = null,
        [Description("Zero-based index when multiple controls match (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Condition: exists, not-exists, enabled, disabled, focused, value-equals, value-contains, or value-changed. Aliases: appears and disappears (default: exists)")] string condition = UiWaitConditions.Exists,
        [Description("Required for value-equals and value-contains; empty string is allowed")] string? expectedValue = null,
        [Description("Maximum wait in milliseconds (default: 10000, hard limit: 300000)")] int timeoutMs = 10_000,
        [Description("Delay between polls in milliseconds (default: 200, range: 25-5000)")] int pollIntervalMs = 200)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (!ValidText(windowHandle, 32))
            return Error("INVALID_REQUEST", "windowHandle is invalid.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            return Error("INVALID_REQUEST", "automationId or name is required.");
        if (!OptionalText(automationId, 1024) || !OptionalText(name, 1024) || !OptionalText(controlType, 128))
            return Error("INVALID_REQUEST", "Selector values exceed their limits or contain control characters.");
        if (occurrenceIndex is < 0 or > 1000)
            return Error("INVALID_REQUEST", "occurrenceIndex must be between 0 and 1000.");
        if (!UiWaitConditions.TryNormalize(condition, out var normalizedCondition))
            return Error("INVALID_REQUEST", "condition must be one of: exists, not-exists, enabled, disabled, focused, value-equals, value-contains, value-changed. Aliases: appears, disappears.");
        if (UiWaitConditions.RequiresExpectedValue(normalizedCondition) && expectedValue is null)
            return Error("INVALID_REQUEST", "expectedValue is required for value-equals and value-contains conditions.");
        if (expectedValue?.Length > MaxExpectedValueCharacters)
            return Error("INVALID_REQUEST", $"expectedValue must be at most {MaxExpectedValueCharacters} characters.");
        if (timeoutMs is < 1 or > MaxTimeoutMs)
            return Error("INVALID_REQUEST", $"timeoutMs must be between 1 and {MaxTimeoutMs}.");
        if (pollIntervalMs is < 25 or > 5000)
            return Error("INVALID_REQUEST", "pollIntervalMs must be between 25 and 5000.");

        var command = new UiWaitCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = windowHandle,
            AutomationId = automationId,
            Name = name,
            ControlType = controlType,
            OccurrenceIndex = occurrenceIndex,
            Condition = normalizedCondition,
            ExpectedValue = expectedValue,
            TimeoutMs = timeoutMs,
            PollIntervalMs = pollIntervalMs
        };

        try
        {
            var result = await _dispatcher.SendAsync<UiWaitResult>(command, RequestToken());
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
            _logger.LogError(ex, "Unexpected ui_wait failure for device {DeviceId}", deviceId);
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

