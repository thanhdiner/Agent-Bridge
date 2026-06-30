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
public sealed partial class WindowCoordinateTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<WindowCoordinateTools> _logger;

    public WindowCoordinateTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<WindowCoordinateTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(Name = "window_click", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Clicks one point relative to a Windows window after validation. Requires dev:execute scope.")]
    public async Task<CallToolResult> ClickWindowAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent.")] string? deviceId,
        [Description("The target native window handle")] string windowHandle,
        [Description("Zero-based horizontal coordinate relative to the window")] int x,
        [Description("Zero-based vertical coordinate relative to the window")] int y,
        [Description("Mouse button: left, right, or middle")] string button = WindowMouseButtons.Left,
        [Description("Number of clicks from 1 to 3")] int clickCount = 1,
        [Description("Optional exact process id guard")] int? expectedProcessId = null,
        [Description("Optional exact window title guard")] string? expectedWindowTitle = null)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(windowHandle) || windowHandle.Length > 32 || windowHandle.Any(char.IsControl))
            return Error("INVALID_REQUEST", "windowHandle is invalid.");
        if (x is < 0 or > 100000 || y is < 0 or > 100000)
            return Error("INVALID_REQUEST", "x and y must be between 0 and 100000.");

        var normalizedButton = button?.Trim().ToLowerInvariant();
        if (!WindowMouseButtons.IsSupported(normalizedButton))
            return Error("INVALID_REQUEST", "button must be left, right, or middle.");
        if (clickCount is < 1 or > 3)
            return Error("INVALID_REQUEST", "clickCount must be between 1 and 3.");
        if (expectedProcessId is <= 0)
            return Error("INVALID_REQUEST", "expectedProcessId must be greater than zero when provided.");
        if (expectedWindowTitle is not null && (expectedWindowTitle.Length > 1024 || expectedWindowTitle.Any(char.IsControl)))
            return Error("INVALID_REQUEST", "expectedWindowTitle is invalid.");

        var command = new WindowClickCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = windowHandle,
            X = x,
            Y = y,
            Button = normalizedButton!,
            ClickCount = clickCount,
            ExpectedProcessId = expectedProcessId,
            ExpectedWindowTitle = expectedWindowTitle
        };

        try
        {
            var result = await _dispatcher.SendAsync<WindowClickResult>(command, CancellationToken());
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
            _logger.LogError(ex, "Unexpected window coordinate action failure for device {DeviceId}", deviceId);
            return Error("INTERNAL_ERROR", "An unexpected error occurred on the gateway.");
        }
    }

    private async Task<bool> AuthorizedAsync()
    {
        var principal = _httpContextAccessor?.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
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
