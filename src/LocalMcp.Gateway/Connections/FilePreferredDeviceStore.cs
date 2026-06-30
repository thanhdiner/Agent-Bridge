using System.Text.Json;
using LocalMcp.BuildingBlocks.Configuration;

namespace LocalMcp.Gateway.Connections;

public sealed class FilePreferredDeviceStore : IPreferredDeviceStore
{
    private readonly object _syncRoot = new();
    private readonly string _path;

    public FilePreferredDeviceStore(string? path = null)
    {
        _path = path ?? LocalConfigurationPaths.GetPreferredDeviceFilePath();
    }

    public string? GetPreferredDeviceId()
    {
        lock (_syncRoot)
        {
            try
            {
                if (!File.Exists(_path))
                    return null;

                var json = File.ReadAllText(_path);
                var state = JsonSerializer.Deserialize<PreferredDeviceState>(json);
                return string.IsNullOrWhiteSpace(state?.DeviceId)
                    ? null
                    : state.DeviceId.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    public void SetPreferredDeviceId(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var normalized = deviceId.Trim();
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
            throw new ArgumentException("Device id is invalid.", nameof(deviceId));

        lock (_syncRoot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var state = new PreferredDeviceState(normalized, DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(state);
            File.WriteAllText(_path, json);
        }
    }

    public void ClearPreferredDeviceId()
    {
        lock (_syncRoot)
        {
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record PreferredDeviceState(
        string DeviceId,
        DateTimeOffset UpdatedAtUtc);
}
