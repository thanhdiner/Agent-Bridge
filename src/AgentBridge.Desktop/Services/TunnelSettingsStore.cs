using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LocalMcp.BuildingBlocks.Configuration;

namespace AgentBridge.Desktop.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TunnelProviderType
{
    Auto,
    Ngrok,
    Cloudflare
}

public sealed record TunnelSettings(
    TunnelProviderType Provider = TunnelProviderType.Auto,
    string NgrokDomain = "",
    string NgrokAuthToken = "",
    string NgrokPath = "",
    string CloudflareTunnelName = "localmcp",
    string CloudflarePath = "",
    string CustomPublicUrl = "")
{
    public static TunnelSettings Default { get; } = new();
}

public sealed class TunnelSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;

    public TunnelSettingsStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(LocalConfigurationPaths.GetApplicationDataDirectory(), "tunnel.json")
            : Path.GetFullPath(path);
    }

    public string ConfigurationPath => _path;

    public async Task<TunnelSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return TunnelSettings.Default;

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<TunnelSettings>(stream, JsonOptions, cancellationToken)
                ?? TunnelSettings.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            await DesktopLog.WriteAsync("Could not load Tunnel settings.", ex, cancellationToken);
            return TunnelSettings.Default;
        }
    }

    public async Task SaveAsync(TunnelSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Tunnel settings path has no parent directory.");
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
