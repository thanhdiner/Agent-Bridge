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
public sealed class AppOpenTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<AppOpenTools> _logger;

    public AppOpenTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<AppOpenTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "app_open",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true),
     Description("Resolves a short Windows application id, focuses an existing matching window when possible, otherwise launches the trusted GUI executable directly. Supports built-in aliases such as youtube. Does not elevate. Requires dev:execute scope.")]
    public async Task<CallToolResult> OpenAppAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("Short application id or name, such as chrome, vscode, obsidian, or antigravity")] string appId,
        [Description("Optional argument array passed directly to the application (maximum 64 entries)")] List<string>? arguments = null,
        [Description("Bypass the cached entry and rediscover only this application id (default: false)")] bool refresh = false,
        [Description("Focus an existing matching application window instead of starting another process when no arguments are supplied (default: true)")] bool focusIfRunning = true,
        [Description("Whether to wait for an application window after launch (default: true)")] bool waitForWindow = true,
        [Description("Optional case-insensitive substring required in the detected window title")] string? windowTitleContains = null,
        [Description("Maximum window wait in milliseconds (default: 15000, hard limit: 300000)")] int timeoutMs = 15_000,
        [Description("Delay between polls in milliseconds (default: 100, range: 25-5000)")] int pollIntervalMs = 100)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(deviceId))
            return Error("INVALID_REQUEST", "deviceId parameter is required.");
        if (string.IsNullOrWhiteSpace(appId))
            return Error("INVALID_REQUEST", "appId parameter is required.");

        var command = new AppOpenCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            AppId = appId,
            Arguments = arguments ?? [],
            Refresh = refresh,
            FocusIfRunning = focusIfRunning,
            WaitForWindow = waitForWindow,
            WindowTitleContains = windowTitleContains,
            TimeoutMs = timeoutMs,
            PollIntervalMs = pollIntervalMs
        };

        try
        {
            var result = await _dispatcher.SendAsync<AppOpenResult>(command, RequestToken());
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
            _logger.LogError(ex, "Unexpected app_open failure for device {DeviceId}", deviceId);
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

    private static CallToolResult Error(string code, string message) =>
        new()
        {
            Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
            IsError = true
        };
}
