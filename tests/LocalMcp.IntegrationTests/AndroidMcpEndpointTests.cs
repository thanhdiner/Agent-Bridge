using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LocalMcp.IntegrationTests;

public sealed class AndroidMcpEndpointTests : IClassFixture<GatewayHealthFactory>
{
    private readonly GatewayHealthFactory _factory;

    public AndroidMcpEndpointTests(GatewayHealthFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AndroidEndpoint_HasIndependentToolCatalog()
    {
        using var client = _factory.CreateClient();
        var accessToken = _factory.CreateAccessToken();

        var androidTools = await ListToolsAsync(client, "/mcp/android/a", accessToken);
        var desktopATools = await ListToolsAsync(client, "/mcp/a", accessToken);
        var desktopBTools = await ListToolsAsync(client, "/mcp/b", accessToken);

        Assert.Equal(9, androidTools.Count);
        Assert.All(androidTools, name => Assert.StartsWith("android_", name, StringComparison.Ordinal));
        Assert.Contains("android_screenshot", androidTools);
        Assert.Contains("android_tap", androidTools);
        Assert.DoesNotContain(desktopATools, name => name.StartsWith("android_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(desktopBTools, name => name.StartsWith("android_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AndroidProtectedResourceMetadata_TargetsAndroidEndpoint()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/.well-known/oauth-protected-resource/mcp/android/a");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith("/mcp/android/a", JsonDocument.Parse(json).RootElement.GetProperty("resource").GetString(), StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<string>> ListToolsAsync(HttpClient client, string endpoint, string accessToken)
    {
        using var initialize = await SendAsync(client, endpoint, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "agentbridge-tests", version = "1.0" }
            }
        }, accessToken: accessToken);
        var initializeBody = await initialize.Content.ReadAsStringAsync();
        Assert.True(
            initialize.StatusCode == HttpStatusCode.OK,
            $"Initialize {endpoint} returned {(int)initialize.StatusCode}: {initializeBody}");
        var sessionId = initialize.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.Single()
            : null;

        using var list = await SendAsync(client, endpoint, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list",
            @params = new { }
        }, sessionId, accessToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var payload = ExtractJson(await list.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToArray();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string endpoint,
        object body,
        string? sessionId = null,
        string? accessToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("Accept", "application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-03-26");
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrWhiteSpace(sessionId))
            request.Headers.Add("Mcp-Session-Id", sessionId);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static JsonDocument ExtractJson(string responseBody)
    {
        var dataLine = responseBody.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("data:", StringComparison.OrdinalIgnoreCase));
        return JsonDocument.Parse(dataLine is null ? responseBody : dataLine[5..].Trim());
    }
}
