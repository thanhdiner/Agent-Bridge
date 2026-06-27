using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using LocalMcp.Gateway.Mcp;
using LocalMcp.Gateway.Commands;
using LocalMcp.Gateway.Security;
using LocalMcp.Contracts.Commands;
using ModelContextProtocol.Protocol;

namespace LocalMcp.UnitTests;

/// <summary>
/// Verifies scope enforcement at the real HTTP transport layer and tool level.
///
/// The MCP SDK uses content-negotiation on the HTTP transport, so tests that
/// send raw `POST /` with a well-formed token but a non-MCP body may get
/// 4xx statuses from the SDK's content-negotiation before reaching the tool.
/// The correct boundary to test HTTP scope enforcement is:
///   - No token → 401 (guaranteed before content-negotiation)
///   - Valid token → NOT 401 and NOT 403 (scope allows access; SDK may still
///     reject a malformed body with 4xx, but that is not an auth rejection)
///
/// Tool-level scope enforcement (FORBIDDEN in MCP payload) is tested
/// directly via the tool class, which avoids HTTP content-negotiation noise.
/// </summary>
[Collection("Sequential")]
public sealed class McpAuthorizationTests : IAsyncDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly string _issuer = "https://auth-test.local";
    private readonly string _audience = "https://mcp-test.local";

    public McpAuthorizationTests()
    {
        var keyBytes = new byte[32];
        RandomNumberGenerator.Fill(keyBytes);
        _signingKey = new SymmetricSecurityKey(keyBytes);
    }

    private async Task StartServerAsync()
    {
        var config = new Dictionary<string, string?>
        {
            { "Security:AuthenticationEnabled",      "true" },
            { "Security:PublicExposure",             "true" },
            { "Security:PublicBaseUrl",              _audience },
            { "Security:OAuth:Authority",            _issuer },
            { "Security:OAuth:Audience",             _audience },
            { "Security:OAuth:RequiredScopes:0",     "files:read" },
            { "AgentSecurity:AuthenticationEnabled", "false" }
        };

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(config);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));

        builder.Services.AddGatewayServices(builder.Configuration);
        builder.Services.AddSignalR();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<FileSystemTools>();

        // Bypass OIDC discovery
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, opts =>
        {
            opts.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                new OpenIdConnectConfiguration { Issuer = _issuer });
            opts.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _signingKey,
                ClockSkew = TimeSpan.Zero
            };
        });

        _app = builder.Build();
        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapMcp().RequireAuthorization("McpAuthenticatedPolicy");

        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private string MakeToken(string? scope = null, DateTime? expires = null)
    {
        var claims = new List<Claim> { new Claim(JwtRegisteredClaimNames.Sub, "test-user") };
        if (scope is not null)
            claims.Add(new Claim("scope", scope));

        var creds = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: expires ?? DateTime.UtcNow.AddMinutes(15),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ── HTTP transport: anonymous → 401 ──────────────────────────────────────

    [Fact]
    public async Task Anonymous_Request_Returns401()
    {
        await StartServerAsync();
        var resp = await _client!.PostAsync("/", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── HTTP transport: valid token → NOT 401, NOT 403 ───────────────────────

    [Theory]
    [InlineData("files:read")]
    [InlineData("files:write files:read")]
    public async Task ValidReadScope_Token_NotRejectedByAuth(string scope)
    {
        await StartServerAsync();
        _client!.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MakeToken(scope));

        var resp = await _client.PostAsync("/", new StringContent("{}", Encoding.UTF8, "application/json"));

        // Authentication passes. SDK may reject malformed body (406/400), but must NOT be 401 or 403.
        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ValidWriteScope_Token_NotRejectedByAuth()
    {
        await StartServerAsync();
        _client!.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MakeToken("files:write"));

        var resp = await _client.PostAsync("/", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── Tool level: scope enforcement via tool class directly ─────────────────
    //
    // Because the MCP HTTP transport does content-negotiation that rejects
    // bare JSON-RPC payloads before they reach the tool, we test tool-level
    // scope enforcement directly via the FileSystemTools class, which is the
    // same technique used in GatewayAuthTests.McpEndpoint_MissingScope_ReturnsForbiddenAtToolLevel.

    private FileSystemTools MakeToolsWithScope(string? scope)
    {
        var principal = scope is null
            ? new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), "Bearer"))
            : new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("scope", scope) }, "Bearer"));

        var httpContext = new DefaultHttpContext { User = principal };
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };

        return new FileSystemTools(
            _app!.Services.GetRequiredService<ICommandDispatcher>(),
            _app!.Services.GetRequiredService<IAuthorizationService>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileSystemTools>.Instance,
            httpContextAccessor);
    }

    [Fact]
    public async Task ReadTool_WithReadScope_Proceeds()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:read");

        var result = await tools.ReadFileAsync("test-device", "C:\\nonexistent.txt");

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        // Auth passed — the error is a filesystem/agent error, not FORBIDDEN
        Assert.DoesNotContain("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadTool_WithoutScope_ReturnsForbidden()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope(scope: null);

        var result = await tools.ReadFileAsync("test-device", "C:\\test.txt");

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadTool_WithWrongScope_ReturnsForbidden()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("other:scope");

        var result = await tools.ReadFileAsync("test-device", "C:\\test.txt");

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadRangeTool_WithoutReadScope_ReturnsForbidden()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:write");

        var result = await tools.ReadRangeAsync("test-device", "C:\\test.txt", 1, 20);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadRangeTool_WithReadScope_Proceeds()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:read");

        var result = await tools.ReadRangeAsync("test-device", "C:\\nonexistent.txt", 1, 20);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.DoesNotContain("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BatchStatTool_WithWriteScopeOnly_ReturnsForbidden()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:write");

        var result = await tools.BatchStatAsync("test-device", new List<string> { "C:/test.txt" });

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BatchStatTool_WithReadScope_Proceeds()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:read");

        var result = await tools.BatchStatAsync("test-device", new List<string> { "C:/nonexistent.txt" });

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.DoesNotContain("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task BatchStatTool_InvalidPathCount_ReturnsInvalidRequest(int count)
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:read");
        var paths = Enumerable.Range(0, count).Select(index => $"C:/item-{index}.txt").ToList();

        var result = await tools.BatchStatAsync("test-device", paths);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("INVALID_REQUEST", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteTool_WithReadScopeOnly_ReturnsForbidden()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:read");

        var result = await tools.WriteFileAsync("test-device", "C:\\test.txt", "content", null, true);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteTool_WithWriteScope_Proceeds()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:write");

        var result = await tools.WriteFileAsync("test-device", "C:\\nonexistent_test.txt", "hello", null, true);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        // Auth passed — agent is offline / no device, not FORBIDDEN
        Assert.DoesNotContain("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PatchTool_WithReadScopeOnly_ReturnsForbidden()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:read");

        var edits = new List<PatchEdit> { new PatchEdit { OldText = "a", NewText = "b", ReplaceAll = false } };
        var result = await tools.PatchFileAsync("test-device", "C:\\test.txt", "abc", edits);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PatchTool_WithWriteScope_Proceeds()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:write");

        var edits = new List<PatchEdit> { new PatchEdit { OldText = "a", NewText = "b", ReplaceAll = false } };
        var result = await tools.PatchFileAsync("test-device", "C:\\nonexistent.txt", "abc", edits);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.DoesNotContain("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveDirectoryTool_WithReadScopeOnly_ReturnsForbidden()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:read");

        var result = await tools.RemoveDirectoryAsync("test-device", "C:/empty");

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveDirectoryTool_WithWriteScope_Proceeds()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:write");

        var result = await tools.RemoveDirectoryAsync("test-device", "C:/nonexistent");

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.DoesNotContain("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteTool_WithReadScopeOnly_ReturnsForbidden()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:read");

        var result = await tools.DeleteAsync("test-device", "C:\\test.txt");

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteTool_WithWriteScope_Proceeds()
    {
        await StartServerAsync();
        var tools = MakeToolsWithScope("files:write");

        var result = await tools.DeleteAsync("test-device", "C:\\nonexistent.txt");

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.DoesNotContain("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
    }
}
