using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalMcp.Gateway;
using LocalMcp.Gateway.Connections;
using LocalMcp.Gateway.Hubs;
using LocalMcp.Gateway.Mcp;
using LocalMcp.Gateway.Security;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

// Add Gateway services
builder.Services.AddGatewayServices(builder.Configuration);
builder.Services.AddHostedService<ManagedRuntimeControlService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});
// Visibility manager.

// Add SignalR
builder.Services.AddSignalR(options =>
{
    // Set reasonable SignalR message-size limits (10MB is plenty for tool calls and small files)
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
});

// Configure MCP Server
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly()
    .WithListToolsHandler(async (context, cancellationToken) =>
    {
        await Task.CompletedTask;
        var toolCollection = context.Server.ServerOptions!.ToolCollection!;
        var services = context.Server.Services ?? throw new InvalidOperationException("MCP server services are unavailable.");
        var localToolCache = services.GetRequiredService<LocalToolPrimitiveCache>();
        var localPrimitives = toolCollection.ToArray();
        if (localPrimitives.Length > 0)
        {
            localToolCache.Remember(localPrimitives);
        }

        var localTools = localToolCache.ListProtocolTools();
        var router = services.GetRequiredService<IExternalMcpRouter>();
        var visibilityStore = services.GetRequiredService<ToolVisibilityStore>();
        var connection = ResolveMcpConnection(services);
        var externalSnapshot = router.GetCatalogSnapshot();
        var externalTools = externalSnapshot.Tools.ToList();
        var filteredTools = McpShardRuntime.ExportToolsForConnection(localTools, externalTools, visibilityStore, connection);
        var visibleLocalToolCount = filteredTools.Count(tool => !router.IsExternalToolName(tool.Name));
        var visibleExternalToolCount = filteredTools.Count(tool => router.IsExternalToolName(tool.Name));
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("LocalMcp.Gateway.Mcp.CompositeTools");

        visibilityStore.RememberCatalog(localTools, externalTools, externalSnapshot.Servers);
        ToolRuntimeHelpers.SuppressSdkLocalToolAppend(toolCollection, logger);

        logger.LogInformation(
            "Exporting MCP tools for connection {Connection}: local={LocalToolCount}, externalServers={ExternalServerCount}, external={ExternalToolCount}, totalAvailable={TotalToolCount}, shardExported={ShardExportedToolCount}, localVisible={VisibleLocalToolCount}, externalVisible={VisibleExternalToolCount}",
            connection,
            localTools.Count,
            externalSnapshot.Servers.Count,
            externalTools.Count,
            localTools.Count + externalTools.Count,
            filteredTools.Count,
            visibleLocalToolCount,
            visibleExternalToolCount);

        return new ListToolsResult
        {
            Tools = filteredTools.ToList()
        };
    })
    .WithCallToolHandler(async (context, cancellationToken) =>
    {
        var services = context.Server.Services ?? throw new InvalidOperationException("MCP server services are unavailable.");
        var router = services.GetRequiredService<IExternalMcpRouter>();
        var requestedName = context.Params.Name;
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "Error [INVALID_REQUEST]: Tool name is required." }]
            };
        }

        async Task<CallToolResult> InvokeLocalToolAsync(CallToolRequestParams request, CancellationToken token)
        {
            var localToolCache = services.GetRequiredService<LocalToolPrimitiveCache>();
            if (localToolCache.TryGetPrimitive(request.Name, out var cachedLocalTool) && cachedLocalTool is not null)
            {
                return await ToolRuntimeHelpers.InvokeLocalPrimitiveAsync(cachedLocalTool, context, token);
            }

            if (context.Server.ServerOptions!.ToolCollection!.TryGetPrimitive(request.Name, out var localTool))
            {
                if (localTool is not null)
                {
                    localToolCache.Remember(new object[] { localTool });
                    return await ToolRuntimeHelpers.InvokeLocalPrimitiveAsync(localTool, context, token);
                }
            }

            return new CallToolResult
            {
                IsError = true,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = $"Error [UNKNOWN_TOOL]: Tool '{request.Name}' is not registered as a local or external MCP tool."
                    }
                ]
            };
        }

        var visibilityStore = services.GetRequiredService<ToolVisibilityStore>();
        var connection = ResolveMcpConnection(services);
        return await McpShardRuntime.CallToolAsync(
            context.Params,
            connection,
            visibilityStore,
            router,
            InvokeLocalToolAsync,
            cancellationToken);
    });

var app = builder.Build();
var deviceActivationStore = app.Services.GetRequiredService<DeviceActivationStore>();

// ── Public Exposure Guardrail ──────────────────────────────────────────────
// This is a startup guardrail, NOT a replacement for authentication.
// It warns operators when the Gateway may be internet-accessible without auth.
var securityOptions = app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value;
var env = app.Environment;

if (securityOptions.PublicExposure && !securityOptions.AuthenticationEnabled)
{
    if (env.IsDevelopment())
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(
            "⚠️  SECURITY WARNING: Gateway is configured with PublicExposure=true and AuthenticationEnabled=false. " +
            "The MCP endpoint is publicly reachable without any authentication. " +
            "Tool policies allow anonymous access while authentication is disabled. " +
            "Do NOT expose write or project-execution tools in this configuration. " +
            "This configuration is only permitted in the Development environment.");
    }
    else
    {
        // In Staging or Production, fail startup immediately.
        throw new InvalidOperationException(
            "STARTUP REJECTED: Security:PublicExposure is true and Security:AuthenticationEnabled is false " +
            "in a non-Development environment. This configuration risks exposing the filesystem to the public " +
            "without any authentication. Enable authentication or set PublicExposure=false before deploying.");
    }
}
// ──────────────────────────────────────────────────────────────────────────

// Intercept GET probe requests on /mcp endpoints before UseRouting short-circuits with 405
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (HttpMethods.IsGet(context.Request.Method) &&
        (path.Equals("/mcp", StringComparison.OrdinalIgnoreCase) ||
         path.Equals("/mcp/a", StringComparison.OrdinalIgnoreCase) ||
         path.Equals("/mcp/b", StringComparison.OrdinalIgnoreCase)))
    {
        var security = context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;
        if (security.AuthenticationEnabled)
        {
            var authService = context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationService>();
            var authResult = await authService.AuthenticateAsync(context, Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
            if (!authResult.Succeeded)
            {
                var isB = path.EndsWith("/mcp/b", StringComparison.OrdinalIgnoreCase);
                var metadataSuffix = isB
                    ? "/.well-known/oauth-protected-resource/mcp/b"
                    : "/.well-known/oauth-protected-resource";

                var endpointRealm = isB ? $"{security.PublicBaseUrl.TrimEnd('/')}/mcp/b" : $"{security.PublicBaseUrl.TrimEnd('/')}/mcp/a";
                var metadataUrl = $"{security.PublicBaseUrl.TrimEnd('/')}{metadataSuffix}";
                var scopesStr = string.Join(" ", security.OAuth.RequiredScopes.Distinct());
                context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
                context.Response.Headers.Append("WWW-Authenticate", $"Bearer realm=\"{endpointRealm}\", resource_metadata=\"{metadataUrl}\", scope=\"{scopesStr}\"");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"unauthorized\",\"message\":\"Authentication required\"}");
                return;
            }
        }

        context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"status\":\"ok\",\"transport\":\"streamable-http\"}");
        return;
    }

    await next(context);
});

// Sanitize ChatGPT / MCP client per-request metadata that triggers protocol version mismatch in SDK
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (HttpMethods.IsPost(context.Request.Method) &&
        (path.Equals("/mcp", StringComparison.OrdinalIgnoreCase) ||
         path.Equals("/mcp/a", StringComparison.OrdinalIgnoreCase) ||
         path.Equals("/mcp/b", StringComparison.OrdinalIgnoreCase)))
    {
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var bodyText = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;

        if (!string.IsNullOrWhiteSpace(bodyText) &&
            bodyText.Contains("_meta") &&
            bodyText.Contains("clientCapabilities"))
        {
            try
            {
                var node = JsonNode.Parse(bodyText);
                if (node is JsonObject obj &&
                    obj.TryGetPropertyValue("params", out var paramsNode) &&
                    paramsNode is JsonObject paramsObj &&
                    paramsObj.TryGetPropertyValue("_meta", out var metaNode) &&
                    metaNode is JsonObject metaObj)
                {
                    var keysToRemove = metaObj
                        .Select(kv => kv.Key)
                        .Where(k => k.Contains("clientCapabilities", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (keysToRemove.Count > 0)
                    {
                        foreach (var key in keysToRemove)
                        {
                            metaObj.Remove(key);
                        }

                        var sanitizedJson = node.ToJsonString();
                        var bytes = Encoding.UTF8.GetBytes(sanitizedJson);
                        context.Request.Body = new MemoryStream(bytes);
                    }
                }
            }
            catch
            {
                context.Request.Body.Position = 0;
            }
        }
    }

    await next(context);
});

app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// ── Protected Resource Metadata (RFC 9728) ─────────────────────────────────
var metadataHandler = (IOptions<SecurityOptions> options, HttpContext context) =>
{
    var security = options.Value;
    var publicUrl = security.PublicBaseUrl.TrimEnd('/');
    var path = context.Request.Path.Value ?? string.Empty;

    string targetResource;
    if (context.Request.Query.TryGetValue("resource", out var reqResource) && !string.IsNullOrWhiteSpace(reqResource))
    {
        targetResource = reqResource.ToString();
    }
    else if (path.EndsWith("/mcp/b", StringComparison.OrdinalIgnoreCase))
    {
        targetResource = $"{publicUrl}/mcp/b";
    }
    else
    {
        targetResource = $"{publicUrl}/mcp/a";
    }

    var response = new
    {
        resource = targetResource,
        authorization_servers = new[] { security.OAuth.Authority.TrimEnd('/') },
        scopes_supported = new[] { "files:read", "files:write", "dev:execute" },
        resource_documentation = $"{publicUrl}/docs"
    };
    return Results.Json(response, contentType: "application/json");
};

app.MapGet("/.well-known/oauth-protected-resource", metadataHandler).AllowAnonymous();
app.MapGet("/.well-known/oauth-protected-resource/mcp", metadataHandler).AllowAnonymous();
app.MapGet("/.well-known/oauth-protected-resource/mcp/a", metadataHandler).AllowAnonymous();
app.MapGet("/.well-known/oauth-protected-resource/mcp/b", metadataHandler).AllowAnonymous();
// ──────────────────────────────────────────────────────────────────────────

// Local supervisor health probes. The Desktop app binds the Gateway to loopback.
app.MapGet("/healthz", () => Results.Json(new
{
    status = "ok",
    service = "AgentBridge.Gateway",
    contractVersion = 1,
    timestampUtc = DateTimeOffset.UtcNow
})).AllowAnonymous();

app.MapGet("/healthz/agent/{deviceId}", (
    string deviceId,
    IAgentConnectionRegistry registry) =>
{
    var normalizedDeviceId = deviceId?.Trim() ?? string.Empty;
    if (normalizedDeviceId.Length is < 1 or > 256 || normalizedDeviceId.Any(char.IsControl))
    {
        return Results.BadRequest(new
        {
            status = "invalid",
            online = false
        });
    }

    return Results.Json(new
    {
        status = "ok",
        deviceId = normalizedDeviceId,
        online = registry.GetConnectionId(normalizedDeviceId) is not null
    });
}).AllowAnonymous();

app.MapGet("/healthz/chrome-devtools", async (
    IExternalMcpRouter router,
    CancellationToken cancellationToken) =>
{
    var report = await router.CheckHealthAsync(cancellationToken);
    return Results.Json(report);
}).AllowAnonymous();

app.MapGet("/api/tools/visibility", async (
    ToolVisibilityStore visibilityStore,
    LocalToolPrimitiveCache localToolCache,
    IExternalMcpRouter router,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    await Task.CompletedTask;
    var logger = loggerFactory.CreateLogger("LocalMcp.Gateway.Mcp.ToolVisibility");
    var localTools = localToolCache.ListProtocolTools();
    if (localTools.Count == 0)
        localTools = LocalToolCatalog.DiscoverFromAssembly(typeof(Program).Assembly);
    var externalSnapshot = router.GetCatalogSnapshot();
    visibilityStore.RememberCatalog(localTools, externalSnapshot.Tools, externalSnapshot.Servers);
    logger.LogInformation(
        "Tool Visibility catalog refreshed: local={LocalToolCount}, externalServers={ExternalServerCount}, external={ExternalToolCount}, totalAvailable={TotalAvailableToolCount}",
        localTools.Count,
        externalSnapshot.Servers.Count,
        externalSnapshot.Tools.Count,
        localTools.Count + externalSnapshot.Tools.Count);
    return Results.Json(visibilityStore.GetSnapshot());
}).AllowAnonymous();

app.MapPut("/api/tools/visibility", async (
    ToolVisibilityUpdateRequest request,
    ToolVisibilityStore visibilityStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        var snapshot = await visibilityStore.SaveAsync(request, cancellationToken);
        return Results.Json(snapshot);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new
        {
            error = "TOOL_CONNECTION_LIMIT_EXCEEDED",
            message = ex.Message
        });
    }
}).AllowAnonymous();

app.MapGet("/api/devices", (
    IAgentConnectionRegistry registry,
    IPreferredDeviceStore preferredDeviceStore) =>
{
    var preferredDeviceId = preferredDeviceStore.GetPreferredDeviceId();
    var devices = registry.GetActiveDeviceInfos()
        .OrderBy(device => string.IsNullOrWhiteSpace(device.DisplayName) ? device.DeviceId : device.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Select(device => new
        {
            deviceId = device.DeviceId,
            displayName = device.DisplayName,
            label = string.IsNullOrWhiteSpace(device.DisplayName) ? device.DeviceId : device.DisplayName,
            online = true,
            preferred = string.Equals(preferredDeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase),
            connectedAtUtc = device.ConnectedAtUtc
        })
        .ToArray();

    return Results.Json(new
    {
        count = devices.Length,
        preferredDeviceId,
        devices
    });
}).AllowAnonymous();

app.MapPut("/api/devices/preferred/{deviceId}", (
    string deviceId,
    IAgentConnectionRegistry registry,
    IPreferredDeviceStore preferredDeviceStore) =>
{
    var normalizedDeviceId = deviceId?.Trim() ?? string.Empty;
    if (normalizedDeviceId.Length is < 1 or > 256 || normalizedDeviceId.Any(char.IsControl))
    {
        return Results.BadRequest(new
        {
            error = "INVALID_DEVICE_ID"
        });
    }

    var device = registry.GetDevice(normalizedDeviceId);
    if (device is null)
    {
        return Results.NotFound(new
        {
            error = "DEVICE_NOT_ONLINE"
        });
    }

    preferredDeviceStore.SetPreferredDeviceId(normalizedDeviceId);
    return Results.Json(new
    {
        preferredDeviceId = normalizedDeviceId,
        displayName = device.DisplayName,
        label = string.IsNullOrWhiteSpace(device.DisplayName) ? device.DeviceId : device.DisplayName,
        online = true
    });
}).AllowAnonymous();

app.MapDelete("/api/devices/preferred", (IPreferredDeviceStore preferredDeviceStore) =>
{
    preferredDeviceStore.ClearPreferredDeviceId();
    return Results.Json(new
    {
        preferredDeviceId = (string?)null,
        online = false
    });
}).AllowAnonymous();

// Device activation MVP
app.MapPost("/api/device-activation/activate", async (HttpContext httpContext) =>
{
    var request = await httpContext.Request.ReadFromJsonAsync<Dictionary<string, string?>>();
    if (request is null ||
        !request.TryGetValue("accountId", out var accountId) ||
        !request.TryGetValue("deviceId", out var deviceId))
    {
        return Results.BadRequest(new { code = "INVALID_ACTIVATION_REQUEST", message = "accountId and deviceId are required." });
    }

    accountId = accountId?.Trim();
    deviceId = deviceId?.Trim();
    if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(deviceId))
    {
        return Results.BadRequest(new { code = "INVALID_ACTIVATION_REQUEST", message = "accountId and deviceId are required." });
    }

    request.TryGetValue("deviceName", out var requestedDeviceName);
    request.TryGetValue("status", out var requestedStatus);
    request.TryGetValue("activeUntilUtc", out var requestedActiveUntilUtc);
    request.TryGetValue("paidUntil", out var requestedLegacyPaidUntil);
    var deviceName = string.IsNullOrWhiteSpace(requestedDeviceName) ? "This computer" : requestedDeviceName.Trim();
    var status = string.IsNullOrWhiteSpace(requestedStatus) ? "active" : requestedStatus.Trim().ToLowerInvariant();
    var activeUntilUtc = TryParseDateTimeOffset(requestedActiveUntilUtc) ?? TryParseDateTimeOffset(requestedLegacyPaidUntil);
    request.TryGetValue("activationToken", out var requestedActivationToken);
    var activationToken = string.IsNullOrWhiteSpace(requestedActivationToken)
        ? $"act_{Guid.NewGuid():N}{Guid.NewGuid():N}"
        : requestedActivationToken.Trim();

    var record = deviceActivationStore.Activate(
        accountId,
        deviceId,
        deviceName,
        activationToken,
        status,
        activeUntilUtc);

    return Results.Ok(record);
}).AllowAnonymous();

app.MapGet("/api/device-activation/status/{deviceId}", (string deviceId) =>
{
    var normalizedDeviceId = deviceId.Trim();
    if (string.IsNullOrWhiteSpace(normalizedDeviceId))
    {
        return Results.BadRequest(new { code = "INVALID_DEVICE_ID", message = "deviceId is required." });
    }

    var record = deviceActivationStore.GetByDeviceId(normalizedDeviceId);
    if (record is not null)
    {
        return Results.Ok(record);
    }

    return Results.Ok(new
    {
        deviceId = normalizedDeviceId,
        activated = false,
        accountId = (string?)null,
        deviceName = (string?)null,
        status = (string?)null,
        activeUntilUtc = (DateTimeOffset?)null,
        features = Array.Empty<string>(),
        createdAtUtc = (DateTimeOffset?)null,
        updatedAtUtc = (DateTimeOffset?)null
    });
}).AllowAnonymous();

app.MapGet("/api/device-activation/current", (HttpContext httpContext) =>
{
    var activationToken = httpContext.Request.Headers["X-AgentBridge-Activation"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(activationToken))
    {
        return Results.BadRequest(new { code = "ACTIVATION_TOKEN_REQUIRED", message = "X-AgentBridge-Activation header is required." });
    }

    var record = deviceActivationStore.GetByActivationToken(activationToken);
    return record is not null
        ? Results.Ok(record)
        : Results.Ok(new { activated = false });
}).AllowAnonymous();

// Map SignalR Hub
app.MapHub<AgentHub>("/hubs/agent").RequireAuthorization("AgentPolicy");

// Map MCP endpoints (Streamable HTTP Transport)
// Keep the legacy MCP path as Connection A so older ChatGPT connectors fail less abruptly.
app.MapMcp("/mcp").RequireAuthorization("McpAuthenticatedPolicy");
app.MapMcp("/mcp/a").RequireAuthorization("McpAuthenticatedPolicy");
app.MapMcp("/mcp/b").RequireAuthorization("McpAuthenticatedPolicy");

// Handle GET probe requests for MCP endpoints to return 401 OAuth Challenge instead of 405 Method Not Allowed
app.MapGet("/mcp", () => Results.Ok(new { status = "ok", connection = "A", transport = "streamable-http" })).RequireAuthorization("McpAuthenticatedPolicy");
app.MapGet("/mcp/a", () => Results.Ok(new { status = "ok", connection = "A", transport = "streamable-http" })).RequireAuthorization("McpAuthenticatedPolicy");
app.MapGet("/mcp/b", () => Results.Ok(new { status = "ok", connection = "B", transport = "streamable-http" })).RequireAuthorization("McpAuthenticatedPolicy");

static DateTimeOffset? TryParseDateTimeOffset(string? value) =>
    DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

static string ResolveMcpConnection(IServiceProvider services)
{
    var path = services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Path.Value ?? string.Empty;
    return path.StartsWith("/mcp/b", StringComparison.OrdinalIgnoreCase)
        ? ToolVisibilityStore.ConnectionB
        : ToolVisibilityStore.ConnectionA;
}

app.Run();

// Make the implicit Program class visible to integration tests
public partial class Program { }






