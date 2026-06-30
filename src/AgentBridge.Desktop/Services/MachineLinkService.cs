using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentBridge.Desktop.Services;

internal sealed class MachineLinkService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public MachineLinkService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
    }

    public async Task<MachineLinkResponse> LinkAsync(
        string gatewayUrl,
        string accountId,
        string deviceId,
        string deviceName,
        string linkValue,
        CancellationToken cancellationToken = default)
    {
        var request = new Dictionary<string, string?>
        {
            ["accountId"] = accountId,
            ["deviceId"] = deviceId,
            ["deviceName"] = deviceName,
            ["status"] = "active",
            ["activationToken"] = linkValue
        };

        using var response = await _httpClient.PostAsJsonAsync(
            $"{gatewayUrl.TrimEnd('/')}/api/device-activation/activate",
            request,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MachineLinkResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Machine activation response was empty.");
    }

    public async Task<MachineLinkResponse?> GetStatusAsync(
        string gatewayUrl,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        using var response = await _httpClient.GetAsync(
            $"{gatewayUrl.TrimEnd('/')}/api/device-activation/status/{Uri.EscapeDataString(deviceId.Trim())}",
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<MachineLinkResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return result is { Activated: true } ? result : null;
    }

    public async Task<MachineLinkResponse?> GetCurrentAsync(
        string gatewayUrl,
        string linkValue,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{gatewayUrl.TrimEnd('/')}/api/device-activation/current");
        request.Headers.TryAddWithoutValidation("X-AgentBridge-Activation", linkValue);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<MachineLinkResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return result is { Activated: true } ? result : null;
    }
}
