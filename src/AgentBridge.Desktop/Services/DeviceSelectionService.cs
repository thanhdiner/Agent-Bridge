using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentBridge.Desktop.Services;

internal sealed class DeviceSelectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public DeviceSelectionService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
    }

    public async Task<DeviceListResponse> GetDevicesAsync(
        string gatewayUrl,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"{gatewayUrl.TrimEnd('/')}/api/devices",
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        return await JsonSerializer
            .DeserializeAsync<DeviceListResponse>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
               ?? new DeviceListResponse(0, null, []);
    }

    public async Task SetDefaultDeviceAsync(
        string gatewayUrl,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsync(
            $"{gatewayUrl.TrimEnd('/')}/api/devices/preferred/{Uri.EscapeDataString(deviceId)}",
            content: null,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}

internal sealed record DeviceListResponse(
    int Count,
    string? PreferredDeviceId,
    IReadOnlyList<DeviceListItem> Devices);

internal sealed record DeviceListItem(
    string DeviceId,
    string? DisplayName,
    string Label,
    bool Online,
    bool Preferred,
    DateTimeOffset ConnectedAtUtc);

internal sealed class DeviceChoice
{
    public required string DeviceId { get; init; }

    public required string Label { get; init; }

    public bool Online { get; init; }

    public bool Preferred { get; init; }
}
