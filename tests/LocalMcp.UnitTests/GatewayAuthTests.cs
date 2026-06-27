using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using Xunit;
using LocalMcp.Gateway.Security;
using LocalMcp.Gateway.Mcp;
using LocalMcp.Gateway.Commands;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;

namespace LocalMcp.UnitTests;

[Collection("Sequential")]
public sealed class GatewayAuthTests : IAsyncDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly string _issuer = "https://test-auth.local";
    private readonly string _audience = "https://test-mcp.local";
    private readonly string _publicBaseUrl = "https://test-mcp.local";

    public GatewayAuthTests()
    {
        // Generate a 256-bit symmetric key for signing
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        _signingKey = new SymmetricSecurityKey(bytes);
    }

    private async Task SetupServerAsync(bool authEnabled = true, string? publicBaseUrl = "https://test-mcp.local", bool setOAuth = true)
    {
        // Populate configuration before calling AddGatewayServices
        var inMemoryConfig = new Dictionary<string, string?>
        {
            { "Security:AuthenticationEnabled", authEnabled.ToString() },
            { "Security:PublicExposure", "true" },
            { "Security:PublicBaseUrl", publicBaseUrl },
            { "Security:OAuth:Authority", setOAuth ? _issuer : "" },
            { "Security:OAuth:Audience", setOAuth ? _audience : "" },
            { "Security:OAuth:RequiredScopes:0", "files:read" },
            { "AgentSecurity:AuthenticationEnabled", "false" }
        };

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(inMemoryConfig);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Loopback, 0); // Ephemeral port
        });

        // Register gateway services
        builder.Services.AddGatewayServices(builder.Configuration);
        builder.Services.AddSignalR();
        builder.Services.AddMcpServer().WithHttpTransport();

        if (authEnabled)
        {
            // Bypass HTTP OIDC discovery over internet using StaticConfigurationManager
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration
                {
                    Issuer = _issuer,
                });

                options.TokenValidationParameters = new TokenValidationParameters
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
        }

        _app = builder.Build();

        var metadataHandler = (IOptions<SecurityOptions> options) =>
        {
            var security = options.Value;
            var response = new
            {
                resource = security.PublicBaseUrl,
                authorization_servers = new[] { security.OAuth.Authority },
                scopes_supported = security.OAuth.RequiredScopes,
                resource_documentation = $"{security.PublicBaseUrl}/docs"
            };
            return Results.Json(response, contentType: "application/json");
        };

        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseAuthorization();

        _app.MapGet("/.well-known/oauth-protected-resource", metadataHandler).AllowAnonymous();
        _app.MapGet("/.well-known/oauth-protected-resource/mcp", metadataHandler).AllowAnonymous();
        _app.MapMcp().RequireAuthorization("McpAuthenticatedPolicy");

        await _app.StartAsync();

        var boundAddress = _app.Urls.First();
        _client = new HttpClient { BaseAddress = new Uri(boundAddress) };
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            _client.Dispose();
        }
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private string GenerateToken(
        DateTime? expires = null,
        string? audience = null,
        string? issuer = null,
        IEnumerable<Claim>? additionalClaims = null)
    {
        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, "test-user-sub")
        };
        if (additionalClaims != null)
        {
            claims.AddRange(additionalClaims);
        }

        var token = new JwtSecurityToken(
            issuer: issuer ?? _issuer,
            audience: audience ?? _audience,
            claims: claims,
            expires: expires ?? DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ── RFC 9728 Protected-Resource Metadata Tests ────────────────────────────

    [Fact]
    public async Task Metadata_ReturnsCorrectJsonAndPublicAccess()
    {
        await SetupServerAsync();

        var response = await _client!.GetAsync("/.well-known/oauth-protected-resource");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(_publicBaseUrl, root.GetProperty("resource").GetString());
        Assert.Equal(_issuer, root.GetProperty("authorization_servers")[0].GetString());
        Assert.Equal("files:read", root.GetProperty("scopes_supported")[0].GetString());
        Assert.Equal($"{_publicBaseUrl}/docs", root.GetProperty("resource_documentation").GetString());
    }

    [Fact]
    public async Task MetadataAlias_ReturnsSameJson()
    {
        await SetupServerAsync();

        var response = await _client!.GetAsync("/.well-known/oauth-protected-resource/mcp");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(_publicBaseUrl, root.GetProperty("resource").GetString());
    }

    // ── MCP Authorization Tests ───────────────────────────────────────────────

    [Fact]
    public async Task McpEndpoint_MissingToken_Returns401AndWWWAuthenticate()
    {
        await SetupServerAsync();

        var response = await _client!.PostAsync("/", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var authHeader = response.Headers.GetValues("WWW-Authenticate").First();
        Assert.Contains("Bearer", authHeader);
        Assert.Contains($"resource_metadata=\"{_publicBaseUrl}/.well-known/oauth-protected-resource\"", authHeader);
        Assert.Contains("scope=\"files:read\"", authHeader);
        Assert.DoesNotContain("localhost", authHeader); // points to PublicBaseUrl
    }

    [Fact]
    public async Task McpEndpoint_InvalidSignature_Returns401()
    {
        await SetupServerAsync();

        // Use a different key to sign the token
        var badKeyBytes = new byte[32];
        RandomNumberGenerator.Fill(badKeyBytes);
        var badKey = new SymmetricSecurityKey(badKeyBytes);

        var credentials = new SigningCredentials(badKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: new[] { new Claim("scope", "files:read") },
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        var rawToken = new JwtSecurityTokenHandler().WriteToken(token);

        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        var response = await _client.PostAsync("/", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task McpEndpoint_WrongIssuer_Returns401()
    {
        await SetupServerAsync();

        var token = GenerateToken(issuer: "https://evil-issuer.com", additionalClaims: new[] { new Claim("scope", "files:read") });
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.PostAsync("/", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task McpEndpoint_WrongAudience_Returns401()
    {
        await SetupServerAsync();

        var token = GenerateToken(audience: "https://wrong-audience.com", additionalClaims: new[] { new Claim("scope", "files:read") });
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.PostAsync("/", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task McpEndpoint_ExpiredToken_Returns401()
    {
        await SetupServerAsync();

        var token = GenerateToken(expires: DateTime.UtcNow.AddMinutes(-5), additionalClaims: new[] { new Claim("scope", "files:read") });
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.PostAsync("/", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task McpEndpoint_MissingScope_ReturnsForbiddenAtToolLevel()
    {
        await SetupServerAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("scope", "other:scope") }, "Bearer"));
        var httpContext = new DefaultHttpContext { User = principal };
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };

        var mcpTools = new FileSystemTools(
            _app!.Services.GetRequiredService<ICommandDispatcher>(),
            _app.Services.GetRequiredService<IAuthorizationService>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileSystemTools>.Instance,
            httpContextAccessor
        );

        var response = await mcpTools.ReadFileAsync("test-device", "C:\\test.txt");

        Assert.True(response.IsError);
        var textBlock = Assert.IsType<TextContentBlock>(response.Content[0]);
        Assert.Contains("FORBIDDEN", textBlock.Text);
    }

    [Theory]
    [InlineData("files:read")]                        // single scope
    [InlineData("other:scope files:read some:scope")]  // space separated string
    public async Task McpEndpoint_ValidScopeClaim_Succeeds(string scopeClaimValue)
    {
        await SetupServerAsync();

        var token = GenerateToken(additionalClaims: new[] { new Claim("scope", scopeClaimValue) });
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.PostAsync("/", new StringContent("{}", Encoding.UTF8, "application/json"));

        // 200 OK (fails route execution or returns standard response, but passes authorization boundary)
        // Since we send empty body {}, ModelContextProtocol parser might return bad request or similar,
        // but the HTTP status will NOT be 401 or 403. Let's assert it is not 401 and not 403.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task McpEndpoint_ValidScpClaimArray_Succeeds()
    {
        await SetupServerAsync();

        // Array of claims is represented as multiple claims of type "scp" in ClaimsPrincipal
        var claims = new[]
        {
            new Claim("scp", "other:scope"),
            new Claim("scp", "files:read")
        };
        var token = GenerateToken(additionalClaims: claims);
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.PostAsync("/", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
