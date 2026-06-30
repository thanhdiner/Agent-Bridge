using LocalMcp.Gateway;
using LocalMcp.Gateway.Connections;
using LocalMcp.Gateway.Hubs;
using LocalMcp.Gateway.Security;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add Gateway services
builder.Services.AddGatewayServices(builder.Configuration);
builder.Services.AddHostedService<ManagedRuntimeControlService>();

// Add SignalR
builder.Services.AddSignalR(options =>
{
    // Set reasonable SignalR message-size limits (10MB is plenty for tool calls and small files)
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
});

// Configure MCP Server
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

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

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// ── Protected Resource Metadata (RFC 9728) ─────────────────────────────────
var metadataHandler = (IOptions<SecurityOptions> options) =>
{
    var security = options.Value;
    var response = new
    {
        resource = security.PublicBaseUrl,
        authorization_servers = new[] { security.OAuth.Authority },
        scopes_supported = new[] { "files:read", "files:write", "dev:execute" },
        resource_documentation = $"{security.PublicBaseUrl}/docs"
    };
    return Results.Json(response, contentType: "application/json");
};

app.MapGet("/.well-known/oauth-protected-resource", metadataHandler).AllowAnonymous();
app.MapGet("/.well-known/oauth-protected-resource/mcp", metadataHandler).AllowAnonymous();
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

// Map SignalR Hub
app.MapHub<AgentHub>("/hubs/agent").RequireAuthorization("AgentPolicy");

// Map MCP endpoints (Streamable HTTP Transport — default path: POST /)
app.MapMcp().RequireAuthorization("McpAuthenticatedPolicy");

app.Run();

// Make the implicit Program class visible to integration tests
public partial class Program { }
