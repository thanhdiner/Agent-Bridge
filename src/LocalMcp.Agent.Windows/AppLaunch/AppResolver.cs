using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalMcp.Agent.Windows.AppLaunch;

public sealed partial class AppResolver : IAppResolver
{
    private const int MaxAppIdCharacters = 128;
    private const long MaxCacheFileBytes = 1_048_576;
    private const int CacheVersion = 1;

    private readonly AppResolverOptions _options;
    private readonly AppLaunchOptions _launchOptions;
    private readonly ILogger<AppResolver> _logger;
    private readonly ConcurrentDictionary<string, AppResolverCacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheLoadGate = new(1, 1);
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private volatile bool _cacheLoaded;

    public AppResolver(
        IOptions<AppResolverOptions> options,
        IOptions<AppLaunchOptions> launchOptions,
        ILogger<AppResolver> logger)
    {
        _options = options.Value;
        _launchOptions = launchOptions.Value;
        _logger = logger;
    }

    public async Task<CommandResult<AppResolveResult>> ResolveAsync(
        string appId,
        bool refresh,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!TryNormalizeAppId(appId, out var normalizedAppId))
        {
            return Failure(
                commandId,
                ErrorCodes.InvalidRequest,
                $"appId must be 1-{MaxAppIdCharacters} characters, contain no control characters, and must not be a path.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return Failure(
                commandId,
                ErrorCodes.AppResolveFailed,
                "Application resolution is only available on Windows agents.");
        }

        try
        {
            await EnsureCacheLoadedAsync(cancellationToken);

            if (!refresh && TryGetValidCachedEntry(normalizedAppId, out var cachedEntry))
            {
                return Success(
                    commandId,
                    appId,
                    normalizedAppId,
                    cachedEntry,
                    cacheHit: true,
                    refreshed: false,
                    stopwatch.ElapsedMilliseconds);
            }

            await _discoveryGate.WaitAsync(cancellationToken);
            try
            {
                if (!refresh && TryGetValidCachedEntry(normalizedAppId, out cachedEntry))
                {
                    return Success(
                        commandId,
                        appId,
                        normalizedAppId,
                        cachedEntry,
                        cacheHit: true,
                        refreshed: false,
                        stopwatch.ElapsedMilliseconds);
                }

                var discovered = Discover(normalizedAppId, cancellationToken);
                if (discovered is not null)
                {
                    _cache[normalizedAppId] = discovered;
                    TrimCache();
                    await PersistCacheBestEffortAsync(cancellationToken);
                }
                else if (refresh && _cache.TryRemove(normalizedAppId, out _))
                {
                    await PersistCacheBestEffortAsync(cancellationToken);
                }

                return Success(
                    commandId,
                    appId,
                    normalizedAppId,
                    discovered,
                    cacheHit: false,
                    refreshed: refresh,
                    stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                _discoveryGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return Failure(commandId, ErrorCodes.CommandCancelled, "The application resolve request was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected application resolution failure for {AppId}", normalizedAppId);
            return Failure(
                commandId,
                ErrorCodes.AppResolveFailed,
                "An unexpected error occurred while resolving the application.");
        }
    }

    internal static bool TryNormalizeAppId(string? appId, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(appId)
            || appId.Length > MaxAppIdCharacters
            || appId.Any(char.IsControl))
        {
            return false;
        }

        var trimmed = appId.Trim();
        if (trimmed.IndexOfAny([
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar,
                Path.VolumeSeparatorChar]) >= 0)
        {
            return false;
        }

        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        normalized = string.Join(
            ' ',
            trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
        return normalized.Length is > 0 and <= MaxAppIdCharacters;
    }

    internal string GetCachePath()
    {
        if (!string.IsNullOrWhiteSpace(_options.CachePath))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(_options.CachePath));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "LocalMcp", "app-catalog-v1.json");
    }

    private bool TryGetValidCachedEntry(string appId, out AppResolverCacheEntry entry)
    {
        entry = null!;
        if (!_cache.TryGetValue(appId, out var candidate))
            return false;

        try
        {
            if (!candidate.RuntimeValidated)
            {
                if (!TryCreateEntry(appId, candidate.ExecutablePath, candidate.Source, out var validated))
                {
                    _cache.TryRemove(appId, out _);
                    return false;
                }

                entry = validated;
                _cache[appId] = entry;
                return true;
            }

            var info = new FileInfo(candidate.ExecutablePath);
            if (!info.Exists
                || info.Length != candidate.FileLength
                || info.LastWriteTimeUtc != candidate.LastWriteTimeUtc.UtcDateTime)
            {
                _cache.TryRemove(appId, out _);
                return false;
            }

            entry = candidate with { LastAccessedUtc = DateTimeOffset.UtcNow };
            _cache[appId] = entry;
            return true;
        }
        catch
        {
            _cache.TryRemove(appId, out _);
            return false;
        }
    }

    private async Task EnsureCacheLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cacheLoaded)
            return;

        await _cacheLoadGate.WaitAsync(cancellationToken);
        try
        {
            if (_cacheLoaded)
                return;

            var cachePath = GetCachePath();
            try
            {
                var info = new FileInfo(cachePath);
                if (info.Exists && info.Length is > 0 and <= MaxCacheFileBytes)
                {
                    await using var stream = new FileStream(
                        cachePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 16_384,
                        useAsync: true);
                    var document = await JsonSerializer.DeserializeAsync<AppResolverCacheDocument>(
                        stream,
                        JsonOptions.Default,
                        cancellationToken);
                    if (document?.Version == CacheVersion)
                    {
                        foreach (var entry in document.Entries
                                     .OrderByDescending(item => item.LastAccessedUtc)
                                     .Take(_options.MaxCacheEntries))
                        {
                            if (TryNormalizeAppId(entry.AppId, out var normalized)
                                && string.Equals(normalized, entry.AppId, StringComparison.Ordinal)
                                && Path.IsPathRooted(entry.ExecutablePath))
                            {
                                _cache[normalized] = entry;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                _logger.LogDebug(ex, "Application resolver cache could not be loaded from {CachePath}", cachePath);
            }

            _cacheLoaded = true;
        }
        finally
        {
            _cacheLoadGate.Release();
        }
    }

    private async Task PersistCacheBestEffortAsync(CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath();
        var tempPath = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Directory.CreateDirectory(directory);
            var document = new AppResolverCacheDocument
            {
                Version = CacheVersion,
                Entries = _cache.Values
                    .OrderByDescending(entry => entry.LastAccessedUtc)
                    .Take(_options.MaxCacheEntries)
                    .ToList()
            };

            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16_384,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions.Default,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Application resolver cache could not be persisted to {CachePath}", cachePath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    private void TrimCache()
    {
        var overflow = _cache.Count - _options.MaxCacheEntries;
        if (overflow <= 0)
            return;

        foreach (var entry in _cache.Values
                     .OrderBy(item => item.LastAccessedUtc)
                     .Take(overflow))
        {
            _cache.TryRemove(entry.AppId, out _);
        }
    }

    private static CommandResult<AppResolveResult> Success(
        Guid commandId,
        string requestedAppId,
        string normalizedAppId,
        AppResolverCacheEntry? entry,
        bool cacheHit,
        bool refreshed,
        long elapsedMilliseconds) =>
        new()
        {
            CommandId = commandId,
            Success = true,
            Data = new AppResolveResult
            {
                AppId = requestedAppId,
                NormalizedAppId = normalizedAppId,
                Resolved = entry is not null,
                ExecutablePath = entry?.ExecutablePath,
                ProcessName = entry is null
                    ? null
                    : Path.GetFileNameWithoutExtension(entry.ExecutablePath),
                Source = entry?.Source,
                CacheHit = cacheHit,
                Refreshed = refreshed,
                ElapsedMs = (int)Math.Min(int.MaxValue, Math.Max(0, elapsedMilliseconds)),
                LastWriteTimeUtc = entry?.LastWriteTimeUtc
            }
        };

    private static CommandResult<AppResolveResult> Failure(
        Guid commandId,
        string code,
        string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };

    internal sealed record AppResolverCacheEntry
    {
        public required string AppId { get; init; }
        public required string ExecutablePath { get; init; }
        public required string Source { get; init; }
        public long FileLength { get; init; }
        public DateTimeOffset LastWriteTimeUtc { get; init; }
        public DateTimeOffset LastAccessedUtc { get; init; }
        [JsonIgnore]
        public bool RuntimeValidated { get; init; }
    }

    internal sealed record AppResolverCacheDocument
    {
        public int Version { get; init; }
        public List<AppResolverCacheEntry> Entries { get; init; } = [];
    }
}
