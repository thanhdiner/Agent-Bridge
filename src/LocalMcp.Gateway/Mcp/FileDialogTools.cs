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
public sealed class FileDialogTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<FileDialogTools> _logger;

    public FileDialogTools(
        ICommandDispatcher dispatcher,
        IAuthorizationService authorizationService,
        ILogger<FileDialogTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "file_dialog_set_path",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false),
     Description("Sets a path in a Windows Open or Save file dialog using UI Automation, verifies the field, and can optionally press Enter. Automatically locates the standard file-name edit control or accepts an explicit selector. Requires dev:execute scope.")]
    public async Task<CallToolResult> SetPathAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The native file dialog window handle as a decimal string or 0x-prefixed hexadecimal string")] string windowHandle,
        [Description("File or directory path to place in the dialog. Maximum 32767 characters.")] string path,
        [Description("Optional exact automationId for a non-standard dialog file-name field")] string? automationId = null,
        [Description("Optional exact control name for a non-standard dialog file-name field")] string? name = null,
        [Description("Optional exact control type such as Edit")] string? controlType = null,
        [Description("Zero-based index when an explicit selector matches multiple controls (default: 0, hard limit: 1000)")] int occurrenceIndex = 0,
        [Description("Whether to focus the dialog before setting the path (default: true)")] bool focusWindow = true,
        [Description("Whether to press Enter after the path is verified (default: false)")] bool submit = false)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (!ValidText(windowHandle, 32))
            return Error("INVALID_REQUEST", "windowHandle is invalid.");
        if (string.IsNullOrWhiteSpace(path))
            return Error("INVALID_REQUEST", "path parameter is required.");
        if (path.Length > 32_767 || path.Contains('\0'))
            return Error("INVALID_REQUEST", "path must be at most 32767 characters and contain no NUL characters.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name)
            && (!string.IsNullOrWhiteSpace(controlType) || occurrenceIndex != 0))
        {
            return Error("INVALID_REQUEST", "controlType and occurrenceIndex require automationId or name.");
        }
        if (!OptionalText(automationId, 1_024)
            || !OptionalText(name, 1_024)
            || !OptionalText(controlType, 128))
        {
            return Error("INVALID_REQUEST", "Selector values exceed their limits or contain control characters.");
        }
        if (occurrenceIndex is < 0 or > 1_000)
            return Error("INVALID_REQUEST", "occurrenceIndex must be between 0 and 1000.");

        var command = new FileDialogSetPathCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = windowHandle,
            Path = path,
            AutomationId = automationId,
            Name = name,
            ControlType = controlType,
            OccurrenceIndex = occurrenceIndex,
            FocusWindow = focusWindow,
            Submit = submit
        };

        try
        {
            var result = await _dispatcher.SendAsync<FileDialogSetPathResult>(command, RequestToken());
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
            _logger.LogError(ex, "Unexpected file_dialog_set_path failure for device {DeviceId}", deviceId);
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

