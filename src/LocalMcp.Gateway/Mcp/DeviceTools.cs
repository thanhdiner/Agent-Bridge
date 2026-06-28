using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using LocalMcp.Gateway.Connections;
using LocalMcp.BuildingBlocks.Serialization;

namespace LocalMcp.Gateway.Mcp;

[McpServerToolType]
public sealed class DeviceTools
{
    private readonly IAgentConnectionRegistry _registry;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<DeviceTools> _logger;

    public DeviceTools(
        IAgentConnectionRegistry registry,
        IAuthorizationService authorizationService,
        ILogger<DeviceTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _registry = registry;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(Name = "device_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Lists all agent devices that are currently online and connected to the Gateway. No input required. Requires a valid authenticated session (McpAuthenticatedPolicy).")]
    public async Task<CallToolResult> ListDevicesAsync()
    {
        if (!await AuthorizeScopeAsync("McpAuthenticatedPolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. A valid authenticated session is required.");
        }

        var activeDevices = _registry.GetActiveDevices();

        var sorted = activeDevices
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => new DeviceEntry(d, Online: true))
            .ToList();

        var response = new DeviceListResponse(Count: sorted.Count, Devices: sorted);
        var json = JsonSerializer.Serialize(response, JsonOptions.Default);

        _logger.LogDebug("device_list returned {Count} devices", sorted.Count);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }],
            IsError = false
        };
    }

    [McpServerTool(Name = "device_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Returns the online/offline status of a specific agent device by its deviceId. Requires a valid authenticated session (McpAuthenticatedPolicy).")]
    public async Task<CallToolResult> GetDeviceStatusAsync(
        [Description("The unique identifier of the agent device to check")] string deviceId)
    {
        if (!await AuthorizeScopeAsync("McpAuthenticatedPolicy"))
        {
            return CreateErrorResult("FORBIDDEN", "Access denied. A valid authenticated session is required.");
        }

        // Validate deviceId
        if (string.IsNullOrWhiteSpace(deviceId))
            return CreateErrorResult("INVALID_REQUEST", "deviceId parameter is required.");

        deviceId = deviceId.Trim();

        if (deviceId.Length > 256)
            return CreateErrorResult("INVALID_REQUEST", "deviceId must be at most 256 characters.");

        if (deviceId.Any(char.IsControl))
            return CreateErrorResult("INVALID_REQUEST", "deviceId must not contain control characters.");

        // Lookup: GetConnectionId returns null if not online
        var connectionId = _registry.GetConnectionId(deviceId);
        var online = connectionId is not null;

        var response = new DeviceStatusResponse(DeviceId: deviceId, Online: online);
        var json = JsonSerializer.Serialize(response, JsonOptions.Default);

        _logger.LogDebug("device_status for {DeviceId}: online={Online}", deviceId, online);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }],
            IsError = false
        };
    }

    // ──────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────

    private async Task<bool> AuthorizeScopeAsync(string policyName)
    {
        var principal = _httpContextAccessor?.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        var authResult = await _authorizationService.AuthorizeAsync(principal, null, policyName);
        return authResult.Succeeded;
    }

    private static CallToolResult CreateErrorResult(string code, string message)
    {
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
            IsError = true
        };
    }

    // ──────────────────────────────────────────────
    // Response DTOs (internal to this file)
    // ──────────────────────────────────────────────

    private sealed record DeviceEntry(
        [property: JsonPropertyName("deviceId")] string DeviceId,
        [property: JsonPropertyName("online")] bool Online);

    private sealed record DeviceListResponse(
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("devices")] IReadOnlyList<DeviceEntry> Devices);

    private sealed record DeviceStatusResponse(
        [property: JsonPropertyName("deviceId")] string DeviceId,
        [property: JsonPropertyName("online")] bool Online);
}
