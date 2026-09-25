using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalMcp.Gateway.Mcp;

[McpServerToolType]
public sealed class ChromeTargetTools
{
    private static readonly string[] InspectableTargetTypes =
        ["page", "iframe", "service_worker", "background_page", "webview"];

    private readonly ChromeCdpClient _cdp;
    private readonly IAuthorizationService _authorization;
    private readonly IHttpContextAccessor _httpContext;
    private readonly ILogger<ChromeTargetTools> _logger;

    public ChromeTargetTools(
        ChromeCdpClient cdp,
        IAuthorizationService authorization,
        IHttpContextAccessor httpContext,
        ILogger<ChromeTargetTools> logger)
    {
        _cdp = cdp;
        _authorization = authorization;
        _httpContext = httpContext;
        _logger = logger;
    }

    [McpServerTool(Name = "chrome_targets_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true),
     Description("Lists Chrome DevTools targets in the current Chrome session, including extension side panels, service workers, and cross-origin iframe targets. Use urlContains to narrow results. Requires dev:execute scope.")]
    public async Task<CallToolResult> ListTargetsAsync(
        [Description("Optional case-insensitive URL substring, such as a Chrome extension ID or provider host.")] string? urlContains = null,
        [Description("Maximum targets to return (default 100, limit 300).")] int maxResults = 100)
    {
        var denied = await RequireDevExecuteAsync();
        if (denied is not null) return denied;
        if (maxResults is < 1 or > 300) return Error("INVALID_REQUEST", "maxResults must be between 1 and 300.");
        if (urlContains?.Length > 500) return Error("INVALID_REQUEST", "urlContains is too long.");

        try
        {
            var targets = await GetTargetsAsync();
            var filtered = targets.EnumerateArray()
                .Where(target => urlContains is null || GetString(target, "url").Contains(urlContains, StringComparison.OrdinalIgnoreCase))
                .Take(maxResults)
                .Select(target => new
                {
                    targetId = GetString(target, "targetId"),
                    type = GetString(target, "type"),
                    title = GetString(target, "title"),
                    url = GetString(target, "url"),
                    browserContextId = GetString(target, "browserContextId"),
                    attached = target.TryGetProperty("attached", out var attached) && attached.ValueKind == JsonValueKind.True
                }).ToArray();
            return Json(new { targets = filtered, returned = filtered.Length });
        }
        catch (Exception ex) { return Failure(ex); }
    }

    [McpServerTool(Name = "chrome_target_snapshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true),
     Description("Shows a Chrome target's frame IDs and a bounded accessibility snapshot. Accepts extension side panels, service workers (metadata only), pages, and iframe targets from chrome_targets_list. Requires dev:execute scope.")]
    public async Task<CallToolResult> SnapshotAsync(
        [Description("Target ID returned by chrome_targets_list.")] string targetId,
        [Description("Optional frame ID from this target's frame tree; omit for the target's main frame.")] string? frameId = null,
        [Description("Maximum accessibility nodes to return (default 250, limit 1000).")] int maxNodes = 250)
    {
        var denied = await RequireDevExecuteAsync();
        if (denied is not null) return denied;
        if (maxNodes is < 1 or > 1000) return Error("INVALID_REQUEST", "maxNodes must be between 1 and 1000.");
        if (!ValidId(targetId) || (frameId is not null && !ValidId(frameId)))
            return Error("INVALID_REQUEST", "Invalid targetId or frameId.");

        try
        {
            var target = await FindTargetAsync(targetId);
            if (target is null) return Error("TARGET_NOT_FOUND", "The Chrome target is no longer available.");
            var type = GetString(target.Value, "type");
            var url = GetString(target.Value, "url");
            if (type == "service_worker")
                return Json(new { targetId, type, url, frames = Array.Empty<object>(), nodes = Array.Empty<object>() });

            var sessionId = await AttachAsync(targetId);
            try
            {
                var frameTree = await _cdp.SendAsync("Page.getFrameTree", sessionId: sessionId, cancellationToken: RequestToken());
                var frames = new List<JsonElement>();
                if (frameTree.TryGetProperty("frameTree", out var root)) AppendFrames(root, frames);
                if (frameId is not null && !frames.Any(frame => GetString(frame, "id") == frameId))
                    return Error("FRAME_NOT_FOUND", "That frame ID is not in this target. Check for a separate iframe target in chrome_targets_list.");

                var axParams = frameId is null ? new { } : (object)new { frameId };
                var ax = await _cdp.SendAsync("Accessibility.getFullAXTree", axParams, sessionId, RequestToken());
                var nodes = ax.GetProperty("nodes").EnumerateArray()
                    .Where(node => !node.TryGetProperty("ignored", out var ignored) || ignored.ValueKind != JsonValueKind.True)
                    .Take(maxNodes)
                    .Select(node => new
                    {
                        nodeId = GetString(node, "nodeId"),
                        parentId = GetString(node, "parentId"),
                        role = GetNestedValue(node, "role"),
                        name = GetNestedValue(node, "name"),
                        backendDOMNodeId = node.TryGetProperty("backendDOMNodeId", out var backendId) ? backendId.GetInt32() : (int?)null
                    }).ToArray();
                return Json(new
                {
                    targetId, type, url,
                    frames = frames.Select(frame => new
                    {
                        frameId = GetString(frame, "id"),
                        parentId = GetString(frame, "parentId"),
                        url = GetString(frame, "url"),
                        name = GetString(frame, "name")
                    }).ToArray(),
                    nodes,
                    returnedNodes = nodes.Length
                });
            }
            finally { await DetachAsync(sessionId); }
        }
        catch (Exception ex) { return Failure(ex); }
    }

    [McpServerTool(Name = "chrome_target_evaluate", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true),
     Description("Evaluates a JavaScript function in a selected Chrome target or frame, including extension side panels, service workers, and cross-origin iframe targets. Use chrome_targets_list and chrome_target_snapshot to find IDs. Requires dev:execute scope.")]
    public async Task<CallToolResult> EvaluateAsync(
        [Description("Target ID returned by chrome_targets_list.")] string targetId,
        [Description("A JavaScript function expression, for example () => document.title.")] string function,
        [Description("Optional frame ID returned by chrome_target_snapshot. For an out-of-process iframe, use its own target ID instead.")] string? frameId = null)
    {
        var denied = await RequireDevExecuteAsync();
        if (denied is not null) return denied;
        if (!ValidId(targetId) || (frameId is not null && !ValidId(frameId)))
            return Error("INVALID_REQUEST", "Invalid targetId or frameId.");
        if (string.IsNullOrWhiteSpace(function) || function.Length > 100_000)
            return Error("INVALID_REQUEST", "function must contain at most 100,000 characters.");

        try
        {
            var target = await FindTargetAsync(targetId);
            if (target is null) return Error("TARGET_NOT_FOUND", "The Chrome target is no longer available.");
            var type = GetString(target.Value, "type");
            if (frameId is not null && type == "service_worker")
                return Error("INVALID_REQUEST", "A service worker has no frames.");

            var sessionId = await AttachAsync(targetId);
            try
            {
                int? contextId = null;
                if (frameId is not null)
                {
                    var tree = await _cdp.SendAsync("Page.getFrameTree", sessionId: sessionId, cancellationToken: RequestToken());
                    var frames = new List<JsonElement>();
                    if (tree.TryGetProperty("frameTree", out var root)) AppendFrames(root, frames);
                    if (!frames.Any(frame => GetString(frame, "id") == frameId))
                        return Error("FRAME_NOT_FOUND", "That frame ID is not in this target. Check for a separate iframe target in chrome_targets_list.");
                    var world = await _cdp.SendAsync("Page.createIsolatedWorld",
                        new { frameId, worldName = "AgentBridge" }, sessionId, RequestToken());
                    contextId = world.GetProperty("executionContextId").GetInt32();
                }

                var evaluation = await _cdp.SendAsync("Runtime.evaluate", new
                {
                    expression = $"({function})()",
                    contextId,
                    awaitPromise = true,
                    returnByValue = true,
                    userGesture = false
                }, sessionId, RequestToken());
                if (evaluation.TryGetProperty("exceptionDetails", out var exception))
                    return Error("JAVASCRIPT_EXCEPTION", exception.ToString());
                var result = evaluation.GetProperty("result");
                return Json(new
                {
                    targetId,
                    frameId,
                    type = GetString(result, "type"),
                    subtype = GetString(result, "subtype"),
                    value = result.TryGetProperty("value", out var value) ? value.Clone() : default(JsonElement?),
                    description = GetString(result, "description")
                });
            }
            finally { await DetachAsync(sessionId); }
        }
        catch (Exception ex) { return Failure(ex); }
    }

    private async Task<JsonElement> GetTargetsAsync()
    {
        var response = await _cdp.SendAsync("Target.getTargets", cancellationToken: RequestToken());
        return response.GetProperty("targetInfos");
    }

    private async Task<JsonElement?> FindTargetAsync(string targetId)
    {
        foreach (var target in (await GetTargetsAsync()).EnumerateArray())
        {
            if (GetString(target, "targetId") == targetId &&
                InspectableTargetTypes.Contains(GetString(target, "type"), StringComparer.Ordinal))
                return target.Clone();
        }
        return null;
    }

    private async Task<string> AttachAsync(string targetId)
    {
        var response = await _cdp.SendAsync("Target.attachToTarget",
            new { targetId, flatten = true }, cancellationToken: RequestToken());
        return response.GetProperty("sessionId").GetString()!;
    }

    private async Task DetachAsync(string sessionId)
    {
        try { await _cdp.SendAsync("Target.detachFromTarget", new { sessionId }, cancellationToken: RequestToken()); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not detach Chrome target session {SessionId}", sessionId); }
    }

    private static void AppendFrames(JsonElement tree, List<JsonElement> frames)
    {
        var frame = tree.GetProperty("frame");
        frames.Add(frame.Clone());
        if (tree.TryGetProperty("childFrames", out var children))
            foreach (var child in children.EnumerateArray()) AppendFrames(child, frames);
    }

    private static string GetString(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? "" : "";

    private static string GetNestedValue(JsonElement value, string key) =>
        value.TryGetProperty(key, out var nested) ? GetString(nested, "value") : "";

    private static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 100 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    private async Task<CallToolResult?> RequireDevExecuteAsync()
    {
        var principal = _httpContext.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        var authorized = await _authorization.AuthorizeAsync(principal, null, "DevExecutePolicy");
        return authorized.Succeeded ? null : Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
    }

    private CancellationToken RequestToken() => _httpContext.HttpContext?.RequestAborted ?? CancellationToken.None;

    private CallToolResult Failure(Exception ex)
    {
        _logger.LogWarning(ex, "Chrome target inspection failed");
        return Error("CHROME_TARGET_FAILED", ex.Message);
    }

    private static CallToolResult Json(object value) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value) }],
        IsError = false
    };

    private static CallToolResult Error(string code, string message) => new()
    {
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
        IsError = true
    };
}
