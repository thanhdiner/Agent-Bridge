using System.Net;
using System.Net.Http.Json;
using LocalMcp.Gateway.Connections;
using LocalMcp.Gateway.Hubs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LocalMcp.IntegrationTests;

public sealed class GatewayHealthEndpointTests : IClassFixture<GatewayHealthFactory>
{
    private readonly GatewayHealthFactory _factory;

    public GatewayHealthEndpointTests(GatewayHealthFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Healthz_Returns_Healthy_Response()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/healthz");
        var payload = await response.Content.ReadFromJsonAsync<GatewayHealthResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("ok", payload!.Status);
        Assert.NotEqual(default, payload.TimestampUtc);
    }

    [Fact]
    public async Task Agent_Health_Returns_Offline_For_Unknown_Device()
    {
        using var client = _factory.CreateClient();
        var deviceId = $"unknown-{Guid.NewGuid():N}";

        using var response = await client.GetAsync($"/healthz/agent/{deviceId}");
        var payload = await response.Content.ReadFromJsonAsync<AgentHealthResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal(deviceId, payload!.DeviceId);
        Assert.False(payload.Online);
    }

    [Fact]
    public async Task Agent_Health_Reflects_Connection_Registry()
    {
        using var client = _factory.CreateClient();
        var registry = _factory.Services.GetRequiredService<IAgentConnectionRegistry>();
        var deviceId = $"health-{Guid.NewGuid():N}";
        var connectionId = $"connection-{Guid.NewGuid():N}";
        registry.Register(deviceId, connectionId);

        try
        {
            using var response = await client.GetAsync($"/healthz/agent/{deviceId}");
            var payload = await response.Content.ReadFromJsonAsync<AgentHealthResponse>();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(payload);
            Assert.True(payload!.Online);
        }
        finally
        {
            registry.Unregister(connectionId);
        }
    }

    [Theory]
    [InlineData("")]
    public async Task Agent_Health_Rejects_Missing_Device_Id(string deviceId)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/healthz/agent/{deviceId}");

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"Unexpected status code: {response.StatusCode}");
    }

    [Fact]
    public async Task Agent_Health_Rejects_Overlong_Device_Id()
    {
        using var client = _factory.CreateClient();
        var deviceId = new string('a', 257);

        using var response = await client.GetAsync($"/healthz/agent/{deviceId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record GatewayHealthResponse(
        string Status,
        DateTimeOffset TimestampUtc);

    private sealed record AgentHealthResponse(
        string Status,
        string DeviceId,
        bool Online);
}
