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
public sealed class AppLaunchTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<AppLaunchTools> _logger;

    public AppLaunchTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<AppLaunchTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "app_launch",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true),
     Description("Launches one allowlisted or allowed-root Windows GUI .exe directly without a shell. Arguments are passed as an array. Optionally waits for a new or title-changed top-level window. Does not elevate. Requires dev:execute scope.")]
    public async Task<CallToolResult> LaunchAppAsync(
        [Description("The unique identifier of the target agent device")] string deviceId,
        [Description("Absolute .exe path inside AllowedRoots, an exact allowlisted path, or an allowlisted Windows system executable name")] string executable,
        [Description("Optional argument array passed directly to the process (maximum 64 entries)")] List<string>? arguments = null,
        [Description("Optional existing working directory inside AllowedRoots; defaults to the executable directory")] string? workingDirectory = null,
        [Description("Whether to wait for an application window (default: true)")] bool waitForWindow = true,
        [Description("Optional case-insensitive substring required in the detected window title")] string? windowTitleContains = null,
        [Description("Maximum window wait in milliseconds (default: 15000, hard limit: 300000)")] int timeoutMs = 15_000,
        [Description("Delay between polls in milliseconds (default: 100, range: 25-5000)")] int pollIntervalMs = 100)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(deviceId))
            return Error("INVALID_REQUEST", "deviceId parameter is required.");
        if (string.IsNullOrWhiteSpace(executable))
            return Error("INVALID_REQUEST", "executable parameter is required.");

        var command = new AppLaunchCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Executable = executable,
            Arguments = arguments ?? [],
            WorkingDirectory = workingDirectory,
            WaitForWindow = waitForWindow,
            WindowTitleContains = windowTitleContains,
            TimeoutMs = timeoutMs,
            PollIntervalMs = pollIntervalMs
        };

        try
        {
            var result = await _dispatcher.SendAsync<AppLaunchResult>(command, RequestToken());
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
            _logger.LogError(ex, "Unexpected app_launch failure for device {DeviceId}", deviceId);
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
