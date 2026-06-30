using System.Text.Json;
using LocalMcp.BuildingBlocks.Configuration;

namespace LocalMcp.Gateway;

public interface IDeviceActivationStore
{
    bool IsActivated(string deviceId);
}

internal sealed class DeviceActivationStore : IDeviceActivationStore
{
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
        string plan,
        string activationToken)
    {
        var record = new DeviceActivationRecord(
            AccountId: NormalizeRequired(accountId, nameof(accountId), 256),
            DeviceId: NormalizeRequired(deviceId, nameof(deviceId), 256),
            DeviceName: NormalizeOptional(deviceName, "This computer", nameof(deviceName), 128),
            ActivationToken: NormalizeRequired(activationToken, nameof(activationToken), 512),
            Plan: NormalizeOptional(plan, "free", nameof(plan), 64).ToLowerInvariant(),
            Activated: true,
            ActivatedAt: DateTimeOffset.UtcNow);

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
                _recordsByDeviceId[record.DeviceId] = record;
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
}

internal sealed record DeviceActivationRecord(
    string AccountId,
    string DeviceId,
    string DeviceName,
    string ActivationToken,
    string Plan,
    bool Activated,
    DateTimeOffset ActivatedAt);
