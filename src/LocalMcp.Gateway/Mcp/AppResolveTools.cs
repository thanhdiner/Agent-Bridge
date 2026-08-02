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
public sealed class AppResolveTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<AppResolveTools> _logger;

    public AppResolveTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<AppResolveTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "app_resolve",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false),
     Description("Resolves a short Windows application id such as chrome, vscode, or obsidian to a GUI executable. Uses a lazy in-memory and JSON cache, then App Paths, common install locations, and Start Menu shortcuts on cache miss. Does not launch the app. Requires dev:execute scope.")]
    public async Task<CallToolResult> ResolveAppAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Short application id or name, not a path, such as chrome or visual studio code")] string appId,
        [Description("Bypass the cached entry and rediscover only this application id (default: false)")] bool refresh = false)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(appId))
            return Error("INVALID_REQUEST", "appId parameter is required.");

        var command = new AppResolveCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            AppId = appId,
            Refresh = refresh
        };

        try
        {
            var result = await _dispatcher.SendAsync<AppResolveResult>(command, RequestToken());
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
            _logger.LogError(ex, "Unexpected app_resolve failure for device {DeviceId}", deviceId);
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

