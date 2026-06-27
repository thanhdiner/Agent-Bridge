using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using LocalMcp.Gateway.Mcp;
using LocalMcp.Gateway.Commands;
using LocalMcp.Gateway.Security;

namespace LocalMcp.IntegrationTests;

[Collection("Sequential")]
public sealed class McpHttpAuthorizationTests : IAsyncDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly string _issuer = "https://auth-test.local";
    private readonly string _audience = "https://mcp-test.local";

    public McpHttpAuthorizationTests()
    {
        var keyBytes = new byte[32];
        RandomNumberGenerator.Fill(keyBytes);
        _signingKey = new SymmetricSecurityKey(keyBytes);
    }

    private async Task StartServerAsync()
    {
        var config = new Dictionary<string, string?>
        {
            { "Security:AuthenticationEnabled", "true" },
            { "Security:PublicExposure", "true" },
            { "Security:PublicBaseUrl", _audience },
            { "Security:OAuth:Authority", _issuer },
            { "Security:OAuth:Audience", _audience },
            { "Security:OAuth:RequiredScopes:0", "files:read" },
            { "AgentSecurity:AuthenticationEnabled", "false" }
        };

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(config);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));

        builder.Services.AddGatewayServices(builder.Configuration);
        builder.Services.AddSignalR();
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<FileSystemTools>()
            .WithTools<BatchReadTools>();

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

    private string MakeToken(string? scope = null)
    {
        var claims = new List<Claim> { new Claim(JwtRegisteredClaimNames.Sub, "test-user") };
        if (scope is not null)
            claims.Add(new Claim("scope", scope));

        var creds = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<HttpResponseMessage> SendMcpRequestAsync(string? token, string method, string toolName, object arguments)
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = method,
            @params = new
            {
                name = toolName,
                arguments = arguments
            }
        });

        var req = new HttpRequestMessage(HttpMethod.Post, "/");
        if (token is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        req.Headers.Add("Accept", "application/json, text/event-stream");
        req.Headers.Add("MCP-Protocol-Version", "2024-11-05");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        return await _client!.SendAsync(req);
    }

    [Fact]
    public async Task Anonymous_CallingFsRead_Returns401()
    {
        await StartServerAsync();
        var resp = await SendMcpRequestAsync(
            token: null,
            method: "tools/call",
            toolName: "fs_read",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt" }
        );

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task FilesReadScope_CallingFsRead_ReachesDispatch_AgentOffline()
    {
        await StartServerAsync();
        var token = MakeToken("files:read");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_read",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt" }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("AGENT_OFFLINE", body);
    }

    [Fact]
    public async Task FilesReadScope_CallingFsReadRange_ReachesDispatch_AgentOffline()
    {
        await StartServerAsync();
        var token = MakeToken("files:read");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_read_range",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt", startLine = 1, lineCount = 20 }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("AGENT_OFFLINE", body);
    }

    [Fact]
    public async Task WrongScope_CallingFsReadRange_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken("files:write");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_read_range",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt", startLine = 1, lineCount = 20 }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body);
    }

    [Fact]
    public async Task FilesReadScope_CallingFsSearchContext_ReachesDispatch_AgentOffline()
    {
        await StartServerAsync();
        var token = MakeToken("files:read");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_search_context",
            arguments: new
            {
                deviceId = "missing-test-device",
                path = "C:/src",
                query = "needle",
                includeGlobs = new[] { "**/*.cs" }
            });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("AGENT_OFFLINE", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FilesWriteScope_CallingFsSearchContext_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken("files:write");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_search_context",
            arguments: new { deviceId = "missing-test-device", path = "C:/src", query = "needle" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("FORBIDDEN", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FilesReadScope_CallingGitStatus_ReachesDispatch_AgentOffline()
    {
        await StartServerAsync();
        var token = MakeToken("files:read");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "git_status",
            arguments: new { deviceId = "missing-test-device", path = "C:/src/repo" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("AGENT_OFFLINE", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FilesWriteScope_CallingGitStatus_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken("files:write");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "git_status",
            arguments: new { deviceId = "missing-test-device", path = "C:/src/repo" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("FORBIDDEN", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FilesReadScope_CallingGitDiff_ReachesDispatch_AgentOffline()
    {
        await StartServerAsync();
        var token = MakeToken("files:read");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "git_diff",
            arguments: new
            {
                deviceId = "missing-test-device",
                path = "C:/src/repo",
                staged = false,
                includeUntracked = true,
                pathSpecs = new[] { "src/**/*.cs" }
            });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("AGENT_OFFLINE", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FilesWriteScope_CallingGitDiff_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken("files:write");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "git_diff",
            arguments: new { deviceId = "missing-test-device", path = "C:/src/repo" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("FORBIDDEN", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FilesReadScope_CallingFsBatchRead_ReachesDispatch_AgentOffline()
    {
        await StartServerAsync();
        var token = MakeToken("files:read");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_batch_read",
            arguments: new
            {
                deviceId = "missing-test-device",
                paths = new[] { "C:/one.txt", "C:/two.txt" }
            });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("AGENT_OFFLINE", body);
    }

    [Fact]
    public async Task FilesWriteScope_CallingFsBatchRead_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken("files:write");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_batch_read",
            arguments: new
            {
                deviceId = "missing-test-device",
                paths = new[] { "C:/one.txt" }
            });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body);
    }

    [Fact]
    public async Task FilesWriteScope_CallingMultiFileEdit_ReachesDispatch_AgentOffline()
    {
        await StartServerAsync();
        var token = MakeToken("files:write");
        var arguments = new
        {
            deviceId = "missing-test-device",
            items = new[]
            {
                new
                {
                    path = "C:/one.txt",
                    expectedSha256 = new string('0', 64),
                    edits = new[]
                    {
                        new { oldText = "old", newText = "new", replaceAll = false }
                    }
                }
            }
        };
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_batch_patch",
            arguments: arguments);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("AGENT_OFFLINE", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FilesReadScope_CallingMultiFileEdit_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken("files:read");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_batch_patch",
            arguments: new
            {
                deviceId = "missing-test-device",
                items = new[]
                {
                    new
                    {
                        path = "C:/one.txt",
                        expectedSha256 = new string('0', 64),
                        edits = new[]
                        {
                            new { oldText = "old", newText = "new", replaceAll = false }
                        }
                    }
                }
            });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("FORBIDDEN", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FilesReadScope_CallingFsBatchStat_ReachesDispatch_AgentOffline()
    {
        await StartServerAsync();
        var token = MakeToken("files:read");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_batch_stat",
            arguments: new { deviceId = "missing-test-device", paths = new[] { "C:/test.txt", "C:/missing" } }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("AGENT_OFFLINE", body);
    }

    [Fact]
    public async Task FilesWriteScope_CallingFsBatchStat_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken("files:write");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_batch_stat",
            arguments: new { deviceId = "missing-test-device", paths = new[] { "C:/test.txt" } }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body);
    }

    [Fact]
    public async Task FilesReadScope_CallingFsWrite_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken("files:read");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_write",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt", content = "hello", createIfMissing = true }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body);
    }

    [Fact]
    public async Task FilesReadScope_CallingFsPatch_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken("files:read");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_patch",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt", expectedSha256 = "hash", edits = new List<object>() }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body);
    }

    [Fact]
    public async Task FilesWriteScope_CallingFsWrite_ReachesDispatch_AgentOffline()
    {
        await StartServerAsync();
        var token = MakeToken("files:write");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_write",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt", content = "hello", createIfMissing = true }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("AGENT_OFFLINE", body);
    }

    [Fact]
    public async Task FilesWriteScope_CallingFsPatch_ReachesDispatch_AgentOffline()
    {
        await StartServerAsync();
        var token = MakeToken("files:write");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_patch",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt", expectedSha256 = "hash", edits = new[] { new { oldText = "a", newText = "b", replaceAll = false } } }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("AGENT_OFFLINE", body);
    }

    [Fact]
    public async Task WrongScope_CallingFsRead_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken("wrong:scope");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_read",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt" }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body);
    }

    [Fact]
    public async Task MissingScope_CallingFsWrite_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken(scope: null);
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_write",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt", content = "hello", createIfMissing = true }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body);
    }

    [Fact]
    public async Task FilesReadScope_CallingFsStat_ReachesDispatch_AgentOffline()
    {
        await StartServerAsync();
        var token = MakeToken("files:read");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_stat",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt" }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("AGENT_OFFLINE", body);
    }

    [Fact]
    public async Task FilesReadScope_CallingFsMkdir_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken("files:read");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_mkdir",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt", recursive = false }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body);
    }

    [Fact]
    public async Task FilesWriteScope_CallingFsMkdir_ReachesDispatch_AgentOffline()
    {
        await StartServerAsync();
        var token = MakeToken("files:write");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_mkdir",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt", recursive = false }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("AGENT_OFFLINE", body);
    }

    [Fact]
    public async Task WrongScope_CallingFsStat_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken("wrong:scope");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_stat",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt" }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body);
    }

    [Fact]
    public async Task FilesReadScope_CallingFsRmdir_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken("files:read");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_rmdir",
            arguments: new { deviceId = "missing-test-device", path = "C:/empty", missingOk = false }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body);
    }

    [Fact]
    public async Task FilesWriteScope_CallingFsRmdir_ReachesDispatch_AgentOffline()
    {
        await StartServerAsync();
        var token = MakeToken("files:write");
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_rmdir",
            arguments: new { deviceId = "missing-test-device", path = "C:/empty", missingOk = false }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("AGENT_OFFLINE", body);
    }

    [Fact]
    public async Task MissingScope_CallingFsMkdir_ReturnsForbidden()
    {
        await StartServerAsync();
        var token = MakeToken(scope: null);
        var resp = await SendMcpRequestAsync(
            token: token,
            method: "tools/call",
            toolName: "fs_mkdir",
            arguments: new { deviceId = "missing-test-device", path = "C:\\test.txt", recursive = false }
        );

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body);
    }
}
