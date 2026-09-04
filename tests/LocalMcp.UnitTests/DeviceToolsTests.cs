using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using NSubstitute;
using LocalMcp.Gateway.Connections;
using LocalMcp.Gateway.Mcp;

namespace LocalMcp.UnitTests;

/// <summary>
/// Unit tests for <see cref="DeviceTools"/> (device_list and device_status MCP tools).
/// These tests exercise tool logic directly without the HTTP transport layer.
/// </summary>
public sealed class DeviceToolsTests
{
    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static DeviceTools BuildTools(
        IAgentConnectionRegistry registry,
        bool authenticated = true,
        IDeviceResolver? deviceResolver = null)
    {
        // Build a real ASP.NET Core DI container with authorization
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(options =>
        {
            options.AddPolicy("McpAuthenticatedPolicy", policy =>
            {
                if (authenticated)
                    policy.RequireAuthenticatedUser();
                else
                    policy.RequireAssertion(_ => false); // Simulate no-auth failure
            });
        });

        var provider = services.BuildServiceProvider();
        var authService = provider.GetRequiredService<IAuthorizationService>();
        var logger = provider.GetRequiredService<ILogger<DeviceTools>>();
        if (deviceResolver is null)
        {
            deviceResolver = Substitute.For<IDeviceResolver>();
            deviceResolver.Resolve(Arg.Any<string?>()).Returns(call =>
            {
                var requested = call.Arg<string?>();
                return string.IsNullOrWhiteSpace(requested)
                    ? DeviceResolution.Failed("INVALID_REQUEST", "deviceId parameter is required.")
                    : DeviceResolution.Resolved(requested.Trim());
            });
        }

        // Build HttpContext with authenticated or anonymous user
        var principal = authenticated
            ? new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "test-user")],
                "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity()); // unauthenticated

        var httpContext = new DefaultHttpContext { User = principal };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        return new DeviceTools(registry, deviceResolver, authService, logger, accessor);
    }

    private static string GetResponseText(CallToolResult result)
    {
        var block = Assert.IsType<TextContentBlock>(result.Content[0]);
        return block.Text;
    }

    // ──────────────────────────────────────────────
    // device_list tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DeviceList_EmptyRegistry_ReturnsZeroCount()
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        registry.GetActiveDevices().Returns(Array.Empty<string>());

        var tools = BuildTools(registry);
        var result = await tools.ListDevicesAsync();

        Assert.False(result.IsError);

        var text = GetResponseText(result);
        var doc = JsonDocument.Parse(text);

        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("devices").GetArrayLength());
    }

    [Fact]
    public async Task DeviceList_MultipleDevices_ReturnsSortedByDeviceId()
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        registry.GetActiveDevices().Returns(new[] { "zeus", "Ares", "beta", "ALPHA" });

        var tools = BuildTools(registry);
        var result = await tools.ListDevicesAsync();

        Assert.False(result.IsError);

        var text = GetResponseText(result);
        var doc = JsonDocument.Parse(text);

        Assert.Equal(4, doc.RootElement.GetProperty("count").GetInt32());

        var devices = doc.RootElement.GetProperty("devices").EnumerateArray().ToList();
        var ids = devices.Select(d => d.GetProperty("deviceId").GetString()).ToList();

        // OrdinalIgnoreCase sort: ALPHA, Ares, beta, zeus
        Assert.Equal(["ALPHA", "Ares", "beta", "zeus"], ids);

        // All entries have online: true
        foreach (var device in devices)
        {
            Assert.True(device.GetProperty("online").GetBoolean());
        }
    }

    [Fact]
    public async Task DeviceList_NoAuth_ReturnsForbidden()
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        registry.GetActiveDevices().Returns(Array.Empty<string>());

        var tools = BuildTools(registry, authenticated: false);
        var result = await tools.ListDevicesAsync();

        Assert.True(result.IsError);
        var text = GetResponseText(result);
        Assert.Contains("FORBIDDEN", text);
    }

    [Fact]
    public async Task DeviceList_ResponseDoesNotContainConnectionId()
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        registry.GetActiveDevices().Returns(new[] { "device-1" });
        // GetConnectionId should NOT be called at all
        registry.GetConnectionId(Arg.Any<string>()).Returns("some-signalr-conn-id-should-not-appear");

        var tools = BuildTools(registry);
        var result = await tools.ListDevicesAsync();

        Assert.False(result.IsError);

        var text = GetResponseText(result);
        Assert.DoesNotContain("some-signalr-conn-id-should-not-appear", text);
        Assert.DoesNotContain("connectionId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connId", text, StringComparison.OrdinalIgnoreCase);

        // GetConnectionId must never have been called
        registry.DidNotReceive().GetConnectionId(Arg.Any<string>());
    }

    // ──────────────────────────────────────────────
    // device_status tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DeviceStatus_OnlineDevice_ReturnsOnlineTrue()
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        registry.GetConnectionId("device-1").Returns("conn-abc");

        var tools = BuildTools(registry);
        var result = await tools.GetDeviceStatusAsync("device-1");

        Assert.False(result.IsError);

        var doc = JsonDocument.Parse(GetResponseText(result));
        Assert.Equal("device-1", doc.RootElement.GetProperty("deviceId").GetString());
        Assert.True(doc.RootElement.GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task DeviceStatus_OfflineDevice_ReturnsOnlineFalse()
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        registry.GetConnectionId("missing-device").Returns((string?)null);

        var tools = BuildTools(registry);
        var result = await tools.GetDeviceStatusAsync("missing-device");

        // Device not found → still success, just online: false
        Assert.False(result.IsError);

        var doc = JsonDocument.Parse(GetResponseText(result));
        Assert.Equal("missing-device", doc.RootElement.GetProperty("deviceId").GetString());
        Assert.False(doc.RootElement.GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task DeviceStatus_CaseInsensitiveLookup()
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        // The registry itself uses OrdinalIgnoreCase, so "DEVICE-1" matches "device-1"
        registry.GetConnectionId(Arg.Is<string>(s =>
            string.Equals(s, "DEVICE-1", StringComparison.OrdinalIgnoreCase)))
            .Returns("conn-xyz");

        var tools = BuildTools(registry);
        var result = await tools.GetDeviceStatusAsync("DEVICE-1");

        Assert.False(result.IsError);
        var doc = JsonDocument.Parse(GetResponseText(result));
        Assert.True(doc.RootElement.GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task DeviceStatus_OmittedDeviceId_UsesResolvedActiveDevice()
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        registry.GetConnectionId("device-1").Returns("conn-abc");
        var resolver = Substitute.For<IDeviceResolver>();
        resolver.Resolve(null).Returns(DeviceResolution.Resolved("device-1"));

        var tools = BuildTools(registry, deviceResolver: resolver);
        var result = await tools.GetDeviceStatusAsync(null);

        Assert.False(result.IsError);
        var doc = JsonDocument.Parse(GetResponseText(result));
        Assert.Equal("device-1", doc.RootElement.GetProperty("deviceId").GetString());
        Assert.True(doc.RootElement.GetProperty("online").GetBoolean());
    }

    [Theory]
    [InlineData("")]          // empty
    [InlineData("   ")]       // whitespace only
    public async Task DeviceStatus_EmptyDeviceId_ReturnsInvalidRequest(string deviceId)
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        var tools = BuildTools(registry);
        var result = await tools.GetDeviceStatusAsync(deviceId);

        Assert.True(result.IsError);
        var text = GetResponseText(result);
        Assert.Contains("INVALID_REQUEST", text);
    }

    [Fact]
    public async Task DeviceStatus_TooLongDeviceId_ReturnsInvalidRequest()
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        var tools = BuildTools(registry);

        var longId = new string('x', 257);
        var result = await tools.GetDeviceStatusAsync(longId);

        Assert.True(result.IsError);
        Assert.Contains("INVALID_REQUEST", GetResponseText(result));
    }

    [Theory]
    [InlineData("device\x01id")]     // SOH control character
    [InlineData("device\x00id")]     // NUL
    [InlineData("device\x1Fid")]     // US (unit separator)
    [InlineData("device\nid")]       // newline
    [InlineData("device\rid")]       // carriage return
    [InlineData("device\tid")]       // tab
    public async Task DeviceStatus_ControlCharacterInDeviceId_ReturnsInvalidRequest(string deviceId)
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        var tools = BuildTools(registry);
        var result = await tools.GetDeviceStatusAsync(deviceId);

        Assert.True(result.IsError);
        Assert.Contains("INVALID_REQUEST", GetResponseText(result));
    }

    [Fact]
    public async Task DeviceStatus_NoAuth_ReturnsForbidden()
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        var tools = BuildTools(registry, authenticated: false);
        var result = await tools.GetDeviceStatusAsync("any-device");

        Assert.True(result.IsError);
        Assert.Contains("FORBIDDEN", GetResponseText(result));
    }

    [Fact]
    public async Task DeviceStatus_ResponseDoesNotContainConnectionId()
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        registry.GetConnectionId("device-1").Returns("super-secret-signalr-id");

        var tools = BuildTools(registry);
        var result = await tools.GetDeviceStatusAsync("device-1");

        Assert.False(result.IsError);
        var text = GetResponseText(result);

        // The connection ID must never appear in the response
        Assert.DoesNotContain("super-secret-signalr-id", text);
        Assert.DoesNotContain("connectionId", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeviceStatus_DeviceIdWithLeadingTrailingSpaces_IsTrimmed()
    {
        var registry = Substitute.For<IAgentConnectionRegistry>();
        registry.GetConnectionId("device-1").Returns("conn-abc");

        var tools = BuildTools(registry);
        var result = await tools.GetDeviceStatusAsync("  device-1  ");

        Assert.False(result.IsError);
        var doc = JsonDocument.Parse(GetResponseText(result));
        // The returned deviceId should be the trimmed value
        Assert.Equal("device-1", doc.RootElement.GetProperty("deviceId").GetString());
        Assert.True(doc.RootElement.GetProperty("online").GetBoolean());
    }
}
