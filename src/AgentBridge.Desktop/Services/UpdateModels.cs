using System;
using System.IO;

namespace AgentBridge.Desktop.Services;

internal sealed record UpdateCheckResult(
    bool IsSkipped,
    bool IsUpdateAvailable,
    Version CurrentVersion,
    Version? LatestVersion,
    UpdateManifest? Manifest,
    string Message);

internal sealed record DownloadedUpdate(
    string Version,
    string FilePath,
    string Sha256);

internal sealed class UpdateManifest
{
    public string Version { get; init; } = string.Empty;

    public string InstallerUrl { get; init; } = string.Empty;

    public string? InstallerSha256 { get; init; }

    public string? Sha256 { get; init; }

    public string? ReleaseNotesUrl { get; init; }

    public bool Mandatory { get; init; }

    public string ExpectedSha256 => !string.IsNullOrWhiteSpace(InstallerSha256)
        ? InstallerSha256
        : Sha256 ?? string.Empty;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
            throw new InvalidDataException("Update manifest is missing version.");

        if (string.IsNullOrWhiteSpace(InstallerUrl))
            throw new InvalidDataException("Update manifest is missing installerUrl.");

        if (!Uri.TryCreate(InstallerUrl, UriKind.Absolute, out var installerUri)
            || installerUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Update manifest installerUrl must be an absolute HTTPS URL.");
        }

        if (ExpectedSha256.Length != 64)
            throw new InvalidDataException("Update manifest is missing a 64-character SHA-256 hash.");

        foreach (var character in ExpectedSha256)
        {
            if (!Uri.IsHexDigit(character))
                throw new InvalidDataException("Update manifest SHA-256 contains non-hex characters.");
        }
    }
}
