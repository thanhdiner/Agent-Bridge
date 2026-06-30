using System.Text.Json;
using System.Text.Json.Serialization;
using LocalMcp.BuildingBlocks.Configuration;

namespace LocalMcp.Gateway;

public interface IDeviceActivationStore
{
    bool IsActivated(string deviceId);
    DeviceActivationRecord? GetByDeviceId(string deviceId);
}

internal sealed class DeviceActivationStore : IDeviceActivationStore
{
    private static readonly string[] DefaultFeatures = ["filesystem", "window", "uia", "shell", "git"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly Dictionary<string, DeviceActivationRecord> _recordsByDeviceId = new(StringComparer.OrdinalIgnoreCase);

    public DeviceActivationStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(LocalConfigurationPaths.GetApplicationDataDirectory(), "device-activations.json")
            : Path.GetFullPath(path);

        Load();
    }

    public DeviceActivationRecord Activate(
        string accountId,
        string deviceId,
        string deviceName,
        string activationToken,
        string status = "active",
        DateTimeOffset? activeUntilUtc = null,
        IReadOnlyList<string>? features = null)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedStatus = NormalizeOptional(status, "active", nameof(status), 64).ToLowerInvariant();
        var record = new DeviceActivationRecord(
            AccountId: NormalizeRequired(accountId, nameof(accountId), 256),
            DeviceId: NormalizeRequired(deviceId, nameof(deviceId), 256),
            DeviceName: NormalizeOptional(deviceName, "This computer", nameof(deviceName), 128),
            ActivationToken: NormalizeRequired(activationToken, nameof(activationToken), 512),
            Activated: true,
            Status: normalizedStatus,
            ActiveUntilUtc: activeUntilUtc ?? now.AddMonths(1),
            Features: NormalizeFeatures(features),
            CreatedAtUtc: now,
            UpdatedAtUtc: now);

        lock (_sync)
        {
            _recordsByDeviceId[record.DeviceId] = record;
            SaveLocked();
        }

        return record;
    }

    public DeviceActivationRecord? GetByDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        lock (_sync)
        {
            return _recordsByDeviceId.TryGetValue(deviceId.Trim(), out var record)
                ? record
                : null;
        }
    }

    public bool IsActivated(string deviceId) =>
        GetByDeviceId(deviceId) is { Activated: true };

    public DeviceActivationRecord? GetByActivationToken(string activationToken)
    {
        if (string.IsNullOrWhiteSpace(activationToken))
            return null;

        var normalizedToken = activationToken.Trim();
        lock (_sync)
        {
            foreach (var record in _recordsByDeviceId.Values)
            {
                if (string.Equals(record.ActivationToken, normalizedToken, StringComparison.Ordinal))
                    return record;
            }
        }

        return null;
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        try
        {
            var records = JsonSerializer.Deserialize<List<DeviceActivationRecord>>(
                File.ReadAllText(_path),
                JsonOptions) ?? [];

            foreach (var record in records.Where(record => !string.IsNullOrWhiteSpace(record.DeviceId)))
                _recordsByDeviceId[record.DeviceId] = NormalizeLoadedRecord(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            var corruptPath = _path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(_path, corruptPath, overwrite: true);
        }
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + $".{Guid.NewGuid():N}.tmp";
        var records = _recordsByDeviceId.Values
            .OrderBy(record => record.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records, JsonOptions));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static string NormalizeRequired(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);

        return Normalize(value, name, maxLength);
    }

    private static string NormalizeOptional(string? value, string fallback, string name, int maxLength)
    {
        var selected = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return Normalize(selected, name, maxLength);
    }

    private static string Normalize(string value, string name, int maxLength)
    {
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new ArgumentException($"{name} must be at most {maxLength} characters.", name);

        if (normalized.Any(char.IsControl))
            throw new ArgumentException($"{name} must not contain control characters.", name);

        return normalized;
    }

    private static string[] NormalizeFeatures(IReadOnlyList<string>? features)
    {
        var selected = features is { Count: > 0 } ? features : DefaultFeatures;
        return selected
            .Where(feature => !string.IsNullOrWhiteSpace(feature))
            .Select(feature => Normalize(feature, nameof(features), 64).ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DeviceActivationRecord NormalizeLoadedRecord(DeviceActivationRecord record)
    {
        var now = DateTimeOffset.UtcNow;
        var status = record.Status;
        if (string.IsNullOrWhiteSpace(status))
            status = string.IsNullOrWhiteSpace(record.LegacyLicenseStatus)
                ? (record.Activated ? "active" : "expired")
                : record.LegacyLicenseStatus;

        var activeUntilUtc = record.ActiveUntilUtc ?? record.LegacyPaidUntil;
        if (activeUntilUtc is null && !string.IsNullOrWhiteSpace(record.LegacyPlan))
            activeUntilUtc = now.AddMonths(1);

        var createdAtUtc = record.CreatedAtUtc;
        if (createdAtUtc == default)
            createdAtUtc = record.LegacyActivatedAt ?? now;

        return record with
        {
            Status = status.Trim().ToLowerInvariant(),
            ActiveUntilUtc = activeUntilUtc,
            Features = NormalizeFeatures(record.Features),
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc == default ? now : record.UpdatedAtUtc,
            LegacyLicenseKind = null,
            LegacyLicenseStatus = null,
            LegacyPaidUntil = null,
            LegacyUpdatesUntil = null,
            LegacyPlan = null,
            LegacyActivatedAt = null
        };
    }
}

public sealed record DeviceActivationRecord(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("activationToken")] string ActivationToken,
    [property: JsonPropertyName("activated")] bool Activated,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("activeUntilUtc")] DateTimeOffset? ActiveUntilUtc = null,
    [property: JsonPropertyName("features")] IReadOnlyList<string>? Features = null,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc = default,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc = default,
    [property: JsonPropertyName("licenseKind")] string? LegacyLicenseKind = null,
    [property: JsonPropertyName("licenseStatus")] string? LegacyLicenseStatus = null,
    [property: JsonPropertyName("paidUntil")] DateTimeOffset? LegacyPaidUntil = null,
    [property: JsonPropertyName("updatesUntil")] DateTimeOffset? LegacyUpdatesUntil = null,
    [property: JsonPropertyName("plan")] string? LegacyPlan = null,
    [property: JsonPropertyName("activatedAt")] DateTimeOffset? LegacyActivatedAt = null);
