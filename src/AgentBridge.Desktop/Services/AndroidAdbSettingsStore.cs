using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LocalMcp.BuildingBlocks.Configuration;

namespace AgentBridge.Desktop.Services;

internal sealed record AndroidAdbSettings(
    string AdbPath,
    string DeviceIp,
    int? PairingPort,
    int? ConnectionPort)
{
    public static AndroidAdbSettings Empty { get; } = new(string.Empty, string.Empty, null, null);
}

internal sealed class AndroidAdbSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;

    public AndroidAdbSettingsStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(LocalConfigurationPaths.GetApplicationDataDirectory(), "android-adb.json")
            : Path.GetFullPath(path);
    }

    public string ConfigurationPath => _path;

    public async Task<AndroidAdbSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return AndroidAdbSettings.Empty;

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AndroidAdbSettings>(stream, JsonOptions, cancellationToken)
                ?? AndroidAdbSettings.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            await DesktopLog.WriteAsync("Could not load Android ADB settings.", ex, cancellationToken);
            return AndroidAdbSettings.Empty;
        }
    }

    public async Task SaveAsync(AndroidAdbSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Android settings path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
        }
    }
}
