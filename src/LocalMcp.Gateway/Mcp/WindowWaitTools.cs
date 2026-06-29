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
public sealed class WindowWaitTools
{
    private const int MaxSelectorCharacters = 1024;
    private const int MaxTimeoutMs = 300_000;
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<WindowWaitTools> _logger;

    public WindowWaitTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<WindowWaitTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "window_wait",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Waits until a top-level Windows window appears, disappears, becomes foreground, or matches a title. Select by handle, PID, process name, class name, exact title, or title substring. Requires dev:execute scope.")]
    public async Task<CallToolResult> WaitWindowAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("Optional native window handle as a decimal string or 0x-prefixed hexadecimal string")] string? windowHandle = null,
        [Description("Optional exact process ID greater than zero")] int? processId = null,
        [Description("Optional exact process name, case-insensitive; .exe suffix is ignored")] string? processName = null,
        [Description("Optional exact native window class name, case-insensitive")] string? className = null,
        [Description("Optional exact current window title, case-insensitive; empty title is allowed")] string? title = null,
        [Description("Optional substring of the current window title, case-insensitive; empty string is allowed")] string? titleContains = null,
        [Description("Zero-based index when multiple windows match, ordered like window_list (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Condition: exists, not-exists, foreground, title-equals, or title-contains. Aliases: appears, disappears, focused (default: exists)")] string condition = WindowWaitConditions.Exists,
        [Description("Required for title-equals and title-contains; compared against the selected window's live title")] string? expectedTitle = null,
        [Description("Whether invisible or cloaked windows may match (default: false)")] bool includeInvisible = false,
        [Description("Maximum wait in milliseconds (default: 10000, hard limit: 300000)")] int timeoutMs = 10_000,
        [Description("Delay between polls in milliseconds (default: 200, range: 25-5000)")] int pollIntervalMs = 200)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(deviceId))
            return Error("INVALID_REQUEST", "deviceId parameter is required.");

        var hasSelector = !string.IsNullOrWhiteSpace(windowHandle)
            || processId.HasValue
            || !string.IsNullOrWhiteSpace(processName)
            || !string.IsNullOrWhiteSpace(className)
            || title is not null
            || titleContains is not null;
        if (!hasSelector)
            return Error("INVALID_REQUEST", "At least one window selector is required.");
        if (!OptionalNonWhitespaceText(windowHandle, 32))
            return Error("INVALID_REQUEST", "windowHandle must be at most 32 characters without control characters.");
        if (processId is <= 0)
            return Error("INVALID_REQUEST", "processId must be greater than zero.");
        if (!OptionalNonWhitespaceText(processName, MaxSelectorCharacters)
            || !OptionalNonWhitespaceText(className, MaxSelectorCharacters)
            || !OptionalText(title, MaxSelectorCharacters)
            || !OptionalText(titleContains, MaxSelectorCharacters))
        {
            return Error("INVALID_REQUEST", "Window selector values exceed their limits or contain control characters.");
        }
        if (occurrenceIndex is < 0 or > 1000)
            return Error("INVALID_REQUEST", "occurrenceIndex must be between 0 and 1000.");
        if (!WindowWaitConditions.TryNormalize(condition, out var normalizedCondition))
            return Error("INVALID_REQUEST", "condition must be one of: exists, not-exists, foreground, title-equals, title-contains. Aliases: appears, disappears, focused.");
        if (WindowWaitConditions.RequiresExpectedTitle(normalizedCondition) && expectedTitle is null)
            return Error("INVALID_REQUEST", "expectedTitle is required for title-equals and title-contains conditions.");
        if (!OptionalText(expectedTitle, MaxSelectorCharacters))
            return Error("INVALID_REQUEST", "expectedTitle exceeds its limit or contains control characters.");
        if (timeoutMs is < 1 or > MaxTimeoutMs)
            return Error("INVALID_REQUEST", $"timeoutMs must be between 1 and {MaxTimeoutMs}.");
        if (pollIntervalMs is < 25 or > 5000)
            return Error("INVALID_REQUEST", "pollIntervalMs must be between 25 and 5000.");

        var command = new WindowWaitCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = windowHandle,
            ProcessId = processId,
            ProcessName = processName,
            ClassName = className,
            Title = title,
            TitleContains = titleContains,
            OccurrenceIndex = occurrenceIndex,
            Condition = normalizedCondition,
            ExpectedTitle = expectedTitle,
            IncludeInvisible = includeInvisible,
            TimeoutMs = timeoutMs,
            PollIntervalMs = pollIntervalMs
        };

        try
        {
            var result = await _dispatcher.SendAsync<WindowWaitResult>(command, RequestToken());
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
            _logger.LogError(ex, "Unexpected window_wait failure for device {DeviceId}", deviceId);
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

    private static bool OptionalNonWhitespaceText(string? value, int limit) =>
        value is null
        || (!string.IsNullOrWhiteSpace(value)
            && value.Length <= limit
            && !value.Any(char.IsControl));

    private static bool OptionalText(string? value, int limit) =>
        value is null || (value.Length <= limit && !value.Any(char.IsControl));

    private static CallToolResult Error(string code, string message) =>
        new()
        {
            Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
            IsError = true
        };
}
