using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace LocalMcp.BuildingBlocks.Configuration;

public sealed record LocalDeviceIdentity
{
    public required string DeviceId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed class LocalDeviceIdentityStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public LocalDeviceIdentityStore(string? devicePath = null)
    {
        DevicePath = string.IsNullOrWhiteSpace(devicePath)
            ? LocalConfigurationPaths.GetDeviceFilePath()
            : Path.GetFullPath(devicePath);
    }

    public string DevicePath { get; }

    public async Task<LocalDeviceIdentity> LoadOrCreateAsync(
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(DevicePath))
        {
            await using var stream = File.OpenRead(DevicePath);
            var existing = await JsonSerializer.DeserializeAsync<LocalDeviceIdentity>(
                stream,
                SerializerOptions,
                cancellationToken);

            if (existing is not null && IsValidDeviceId(existing.DeviceId))
                return existing;

            throw new InvalidDataException(
                $"The AgentBridge device identity at '{DevicePath}' is invalid.");
        }

        var identity = new LocalDeviceIdentity
        {
            DeviceId = $"device-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var directory = Path.GetDirectoryName(DevicePath)
            ?? throw new InvalidOperationException("The device identity path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(DevicePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(identity, SerializerOptions);
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, DevicePath, overwrite: false);
        }
        catch (IOException) when (File.Exists(DevicePath))
        {
            return await LoadOrCreateAsync(cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return identity;
    }

    private static bool IsValidDeviceId(string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId)
        && deviceId.Length <= 256
        && !deviceId.Any(char.IsControl);
}
