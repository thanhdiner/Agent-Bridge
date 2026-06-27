using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Xunit;
using LocalMcp.Gateway.Hubs;

namespace LocalMcp.UnitTests;

[Collection("Sequential")]
public sealed class AgentAuthTests : IDisposable, IAsyncDisposable
{
    private WebApplication? _app;
    private readonly string _envVarName = "TEST_HUB_TOKEN_" + Guid.NewGuid().ToString("N");
    private readonly string _validToken = "valid-hub-secret-token";

    public AgentAuthTests()
    {
        Environment.SetEnvironmentVariable(_envVarName, _validToken);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_envVarName, null);
    }

    private async Task<string> SetupGatewayAsync(bool authEnabled)
    {
        var inMemoryConfig = new Dictionary<string, string?>
        {
            { "Security:AuthenticationEnabled", "false" },
            { "Security:PublicExposure", "true" },
            { "Security:PublicBaseUrl", "https://test-mcp.local" },
            { "AgentSecurity:AuthenticationEnabled", authEnabled.ToString() },
            { "AgentSecurity:TokenEnvironmentVariable", _envVarName }
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

        _app = builder.Build();
        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseAuthorization();

        _app.MapHub<AgentHub>("/hubs/agent").RequireAuthorization("AgentPolicy");

        await _app.StartAsync();
        return _app.Urls.First();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    // ── SignalR Token Validation Tests ────────────────────────────────────────

    [Fact]
    public async Task Hub_WithAuthenticationDisabled_AllowsAnonymousConnections()
    {
        var gatewayUrl = await SetupGatewayAsync(authEnabled: false);
        var hubUrl = $"{gatewayUrl.TrimEnd('/')}/hubs/agent?deviceId=test-dev";

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .Build();

        await connection.StartAsync();
        Assert.Equal(HubConnectionState.Connected, connection.State);

        await connection.StopAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Hub_WithAuthenticationEnabled_AllowsValidToken()
    {
        var gatewayUrl = await SetupGatewayAsync(authEnabled: true);
        var hubUrl = $"{gatewayUrl.TrimEnd('/')}/hubs/agent?deviceId=test-dev";

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_validToken);
            })
            .Build();

        await connection.StartAsync();
        Assert.Equal(HubConnectionState.Connected, connection.State);

        await connection.StopAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Hub_WithAuthenticationEnabled_RejectsMissingToken()
    {
        var gatewayUrl = await SetupGatewayAsync(authEnabled: true);
        var hubUrl = $"{gatewayUrl.TrimEnd('/')}/hubs/agent?deviceId=test-dev";

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .Build();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await connection.StartAsync();
        });

        // The HTTP request gets rejected with 401 Unauthorized
        Assert.Contains("401", exception.Message);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Hub_WithAuthenticationEnabled_RejectsInvalidToken()
    {
        var gatewayUrl = await SetupGatewayAsync(authEnabled: true);
        var hubUrl = $"{gatewayUrl.TrimEnd('/')}/hubs/agent?deviceId=test-dev";

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>("incorrect-token-value");
            })
            .Build();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await connection.StartAsync();
        });

        Assert.Contains("401", exception.Message);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);

        await connection.DisposeAsync();
    }

    // ── Agent Startup Options Validation Tests ────────────────────────────────

    [Fact]
    public void AgentOptions_WithAuthEnabledAndMissingTokenVar_ThrowsValidationException()
    {
        // Temporarily clear environment variable to test missing token validation
        Environment.SetEnvironmentVariable(_envVarName, null);

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddOptions<LocalMcp.Agent.Windows.Connection.AgentSecurityOptions>()
            .Configure(o =>
            {
                o.AuthenticationEnabled = true;
                o.TokenEnvironmentVariable = _envVarName;
            })
            .Validate(o =>
            {
                var token = Environment.GetEnvironmentVariable(o.TokenEnvironmentVariable);
                return !string.IsNullOrWhiteSpace(token);
            }, "Expected token is missing.")
            .ValidateOnStart();

        var provider = services.BuildServiceProvider();

        // Getting the validated options triggers validation at startup
        Assert.Throws<OptionsValidationException>(() =>
        {
            _ = provider.GetRequiredService<IOptions<LocalMcp.Agent.Windows.Connection.AgentSecurityOptions>>().Value;
        });
    }

    [Fact]
    public void AgentOptions_WithAuthEnabledAndValidTokenVar_Succeeds()
    {
        var services = new ServiceCollection();

        services.AddOptions<LocalMcp.Agent.Windows.Connection.AgentSecurityOptions>()
            .Configure(o =>
            {
                o.AuthenticationEnabled = true;
                o.TokenEnvironmentVariable = _envVarName;
            })
            .Validate(o =>
            {
                var token = Environment.GetEnvironmentVariable(o.TokenEnvironmentVariable);
                return !string.IsNullOrWhiteSpace(token);
            }, "Expected token is missing.")
            .ValidateOnStart();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LocalMcp.Agent.Windows.Connection.AgentSecurityOptions>>().Value;

        Assert.True(options.AuthenticationEnabled);
        Assert.Equal(_envVarName, options.TokenEnvironmentVariable);
    }
}
