using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentBridge.Desktop.Services;

internal sealed class UpdateService
{
    private const string DefaultManifestUrl =
        "https://github.com/thanhdiner/Agent-Bridge/releases/latest/download/agentbridge-update.json";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _updatesDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentBridge",
        "Updates");

    private readonly string _lastCheckPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentBridge",
        "Updates",
        "last-check.txt");

    public Version CurrentVersion { get; } = GetCurrentVersion();

    public bool ShouldAutoCheck(TimeSpan interval)
    {
        try
        {
            if (!File.Exists(_lastCheckPath))
                return true;

            var text = File.ReadAllText(_lastCheckPath).Trim();
            if (!DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var lastCheckedAt))
            {
                return true;
            }

            return DateTimeOffset.UtcNow - lastCheckedAt >= interval;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (!force && !ShouldAutoCheck(TimeSpan.FromHours(12)))
        {
            return new UpdateCheckResult(
                IsSkipped: true,
                IsUpdateAvailable: false,
                CurrentVersion,
                LatestVersion: null,
                Manifest: null,
                Message: "Update check skipped until the next interval.");
        }

        var manifestUri = GetManifestUri();
        if (manifestUri is null)
        {
            return new UpdateCheckResult(
                IsSkipped: false,
                IsUpdateAvailable: false,
                CurrentVersion,
                LatestVersion: null,
                Manifest: null,
                Message: "Update feed is not configured.");
        }

        await MarkCheckedAsync(cancellationToken).ConfigureAwait(false);

        using var response = await HttpClient.GetAsync(manifestUri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        var manifest = await JsonSerializer
            .DeserializeAsync<UpdateManifest>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (manifest is null)
            throw new InvalidDataException("Update manifest is empty.");

        manifest.Validate();

        var latestVersion = ParseVersion(manifest.Version)
            ?? throw new InvalidDataException($"Invalid update version: {manifest.Version}");

        return new UpdateCheckResult(
            IsSkipped: false,
            IsUpdateAvailable: latestVersion > CurrentVersion,
            CurrentVersion,
            latestVersion,
            manifest,
            latestVersion > CurrentVersion
                ? $"AgentBridge {latestVersion} is available."
                : $"AgentBridge is up to date. Current version: {CurrentVersion}.");
    }

    public async Task<DownloadedUpdate> DownloadPackageAsync(
        UpdateManifest manifest,
        CancellationToken cancellationToken = default)
    {
        manifest.Validate();
        Directory.CreateDirectory(_updatesDirectory);

        var packageUri = new Uri(manifest.InstallerUrl, UriKind.Absolute);
        if (packageUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Update package URL must use HTTPS.");

        var filePath = Path.Combine(
            _updatesDirectory,
            $"AgentBridgeSetup-{SanitizeFileName(manifest.Version)}.exe");

        using var response = await HttpClient
            .GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (var source = await response.Content
                         .ReadAsStreamAsync(cancellationToken)
                         .ConfigureAwait(false))
        await using (var destination = new FileStream(
                         filePath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 1024 * 128,
                         useAsync: true))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        var actualSha256 = await ComputeSha256Async(filePath, cancellationToken).ConfigureAwait(false);
        if (!actualSha256.Equals(manifest.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Downloaded package SHA-256 mismatch. Expected {manifest.ExpectedSha256}, got {actualSha256}.");
        }

        return new DownloadedUpdate(manifest.Version, filePath, actualSha256);
    }

    private static Uri? GetManifestUri()
    {
        var configuredUrl = Environment.GetEnvironmentVariable("AGENTBRIDGE_UPDATE_MANIFEST_URL");
        var url = string.IsNullOrWhiteSpace(configuredUrl)
            ? DefaultManifestUrl
            : configuredUrl.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        return uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    private async Task MarkCheckedAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_updatesDirectory);
        await File.WriteAllTextAsync(
                _lastCheckPath,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Version GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return ParseVersion(informationalVersion)
               ?? assembly.GetName().Version
               ?? new Version(0, 1, 0);
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[1..];

        var metadataIndex = normalized.IndexOfAny(['+', '-']);
        if (metadataIndex >= 0)
            normalized = normalized[..metadataIndex];

        return Version.TryParse(normalized, out var version)
            ? version
            : null;
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '-');

        return value.Replace(' ', '-');
    }
}
