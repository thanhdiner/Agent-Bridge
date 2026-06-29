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
public sealed class ClipboardTools
{
    private const int MaxClipboardCharacters = 1_048_576;
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<ClipboardTools> _logger;

    public ClipboardTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<ClipboardTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "clipboard_get",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Reads bounded Unicode text from the Windows clipboard. Non-text clipboard contents are reported without conversion. Requires dev:execute scope.")]
    public async Task<CallToolResult> GetAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("Maximum UTF-16 characters returned (default: 65536, hard limit: 1048576)")] int maxCharacters = 65_536)
    {
        var authorizationError = await ValidateAuthorizationAndDeviceAsync(deviceId);
        if (authorizationError is not null)
            return authorizationError;
        if (maxCharacters is < 1 or > MaxClipboardCharacters)
            return Error("INVALID_REQUEST", $"maxCharacters must be between 1 and {MaxClipboardCharacters}.");

        return await DispatchAsync<ClipboardGetResult>(
            new ClipboardGetCommand
            {
                CommandId = Guid.NewGuid(),
                DeviceId = deviceId,
                CreatedAt = DateTimeOffset.UtcNow,
                MaxCharacters = maxCharacters
            },
            "clipboard_get");
    }

    [McpServerTool(
        Name = "clipboard_set",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false),
     Description("Replaces Windows clipboard contents with Unicode text. The text is not echoed in the result. Requires dev:execute scope.")]
    public async Task<CallToolResult> SetAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("Unicode clipboard text. Empty text clears the textual clipboard value. Hard limit: 1048576 UTF-16 characters.")] string text,
        [Description("Whether to read back and verify the clipboard after writing (default: true)")] bool verify = true)
    {
        var authorizationError = await ValidateAuthorizationAndDeviceAsync(deviceId);
        if (authorizationError is not null)
            return authorizationError;
        if (text is null)
            return Error("INVALID_REQUEST", "text parameter is required.");
        if (text.Length > MaxClipboardCharacters || text.Contains('\0'))
            return Error("INVALID_REQUEST", $"text must be at most {MaxClipboardCharacters} characters and contain no NUL characters.");

        return await DispatchAsync<ClipboardSetResult>(
            new ClipboardSetCommand
            {
                CommandId = Guid.NewGuid(),
                DeviceId = deviceId,
                CreatedAt = DateTimeOffset.UtcNow,
                Text = text,
                Verify = verify
            },
            "clipboard_set");
    }

    private async Task<CallToolResult?> ValidateAuthorizationAndDeviceAsync(string deviceId)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(deviceId))
            return Error("INVALID_REQUEST", "deviceId parameter is required.");
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
                    Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result.Data, JsonOptions.Default) }],
                    IsError = false
                };
            }

            return Error(result.Error?.Code ?? "INTERNAL_ERROR", result.Error?.Message ?? "Command execution failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected {ToolName} failure for device {DeviceId}", toolName, command.DeviceId);
            return Error("INTERNAL_ERROR", "An unexpected gateway error occurred.");
        }
    }

    private async Task<bool> AuthorizedAsync()
    {
        var principal = _httpContextAccessor?.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        return (await _authorizationService.AuthorizeAsync(principal, null, "DevExecutePolicy")).Succeeded;
    }

    private CancellationToken RequestToken() =>
        _httpContextAccessor?.HttpContext?.RequestAborted ?? CancellationToken.None;

    private static CallToolResult Error(string code, string message) => new()
    {
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
        IsError = true
    };
}
