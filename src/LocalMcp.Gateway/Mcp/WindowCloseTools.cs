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
public sealed class WindowCloseTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<WindowCloseTools> _logger;

    public WindowCloseTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<WindowCloseTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "window_close",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false),
     Description("Requests a graceful close for one live top-level Windows window. Unsaved-work prompts may keep the window open. Requires dev:execute scope.")]
    public async Task<CallToolResult> CloseWindowAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The target native window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(windowHandle) || windowHandle.Length > 32 || windowHandle.Any(char.IsControl))
            return Error("INVALID_REQUEST", "windowHandle is required and must be at most 32 characters without control characters.");

        var command = new WindowCloseCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = windowHandle
        };

        try
        {
            var result = await _dispatcher.SendAsync<WindowCloseResult>(command, CancellationToken());
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
                result.Error?.Message ?? "An unexpected error occurred during command execution.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing window_close for device {DeviceId}", deviceId);
            return Error("INTERNAL_ERROR", "An unexpected error occurred on the gateway.");
        }
    }

    private async Task<bool> AuthorizedAsync()
    {
        var principal = _httpContextAccessor?.HttpContext?.User
            ?? new ClaimsPrincipal(new ClaimsIdentity());
        return (await _authorizationService.AuthorizeAsync(principal, null, "DevExecutePolicy")).Succeeded;
    }

    private CancellationToken CancellationToken() =>
        _httpContextAccessor?.HttpContext?.RequestAborted ?? System.Threading.CancellationToken.None;

    private static CallToolResult Error(string code, string message) =>
        new()
        {
            Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
            IsError = true
        };
}

