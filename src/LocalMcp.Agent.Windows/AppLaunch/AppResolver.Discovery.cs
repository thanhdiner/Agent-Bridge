using Microsoft.Win32;

namespace LocalMcp.Agent.Windows.AppLaunch;

public sealed partial class AppResolver
{
    private static readonly IReadOnlyDictionary<string, string[]> BuiltInExecutableNames =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = ["chrome.exe"],
            ["google chrome"] = ["chrome.exe"],
            ["edge"] = ["msedge.exe"],
            ["microsoft edge"] = ["msedge.exe"],
            ["firefox"] = ["firefox.exe"],
            ["vscode"] = ["Code.exe"],
            ["visual studio code"] = ["Code.exe"],
            ["code"] = ["Code.exe"],
            ["obsidian"] = ["Obsidian.exe"],
            ["postman"] = ["Postman.exe"],
            ["notepad"] = ["notepad.exe"],
            ["paint"] = ["mspaint.exe"],
            ["mspaint"] = ["mspaint.exe"],
            ["calculator"] = ["calc.exe"],
            ["calc"] = ["calc.exe"],
            ["terminal"] = ["WindowsTerminal.exe", "wt.exe"],
            ["windows terminal"] = ["WindowsTerminal.exe", "wt.exe"]
        };

    private static readonly IReadOnlyDictionary<string, string[]> BuiltInDisplayNames =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = ["google chrome", "chrome"],
            ["google chrome"] = ["google chrome", "chrome"],
            ["edge"] = ["microsoft edge", "edge"],
            ["microsoft edge"] = ["microsoft edge", "edge"],
            ["vscode"] = ["visual studio code", "vscode", "code"],
            ["visual studio code"] = ["visual studio code", "vscode", "code"],
            ["code"] = ["visual studio code", "vscode", "code"],
            ["terminal"] = ["windows terminal", "terminal"],
            ["windows terminal"] = ["windows terminal", "terminal"]
        };

    private AppResolverCacheEntry? Discover(
        string appId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_options.Aliases.TryGetValue(appId, out var explicitAlias)
            && TryResolveConfiguredValue(appId, explicitAlias, "configured-alias", out var configured))
        {
            return configured;
        }

        foreach (var allowedExecutable in _launchOptions.AllowedExecutables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesAllowedExecutable(appId, allowedExecutable))
                continue;
            if (TryResolveConfiguredValue(appId, allowedExecutable, "launch-allowlist", out var allowed))
                return allowed;
        }

        foreach (var executableName in GetExecutableNames(appId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryResolveAppPath(appId, executableName, out var appPath))
                return appPath;
            if (TryResolveSystemDirectory(appId, executableName, out var systemPath))
                return systemPath;
        }

        foreach (var candidate in GetCommonCandidatePaths(appId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryCreateEntry(appId, candidate, "common-location", out var common))
                return common;
        }

        return TryResolveStartMenuShortcut(appId, cancellationToken, out var shortcut)
            ? shortcut
            : null;
    }

    private bool TryResolveConfiguredValue(
        string appId,
        string configuredValue,
        string source,
        out AppResolverCacheEntry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(configuredValue))
            return false;

        var expanded = Environment.ExpandEnvironmentVariables(configuredValue.Trim().Trim('"'));
        if (Path.IsPathRooted(expanded))
            return TryCreateEntry(appId, expanded, source, out entry);

        if (expanded.IndexOfAny([
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar,
                Path.VolumeSeparatorChar]) >= 0)
        {
            return false;
        }

        var executableName = expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? expanded
            : expanded + ".exe";
        if (TryResolveAppPath(appId, executableName, source, out entry)
            || TryResolveSystemDirectory(appId, executableName, source, out entry))
        {
            return true;
        }

        foreach (var path in GetCommonCandidatePaths(appId))
        {
            if (TryCreateEntry(appId, path, source, out entry))
                return true;
        }

        return false;
    }

    private static bool MatchesAllowedExecutable(string appId, string configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
            return false;

        try
        {
            var fileName = Path.GetFileName(configuredValue.Trim().Trim('"'));
            var stem = Path.GetFileNameWithoutExtension(fileName);
            return TryNormalizeAppId(stem, out var normalized)
                && string.Equals(normalized, appId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> GetExecutableNames(string appId)
    {
        var names = new List<string>();
        if (BuiltInExecutableNames.TryGetValue(appId, out var builtIns))
            names.AddRange(builtIns);

        var compact = appId.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compact.Length > 0)
            names.Add(compact + ".exe");

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool TryResolveAppPath(
        string appId,
        string executableName,
        out AppResolverCacheEntry entry) =>
        TryResolveAppPath(appId, executableName, "app-paths", out entry);

    private bool TryResolveAppPath(
        string appId,
        string executableName,
        string source,
        out AppResolverCacheEntry entry)
    {
        entry = null!;
        if (!OperatingSystem.IsWindows())
            return false;

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(
                        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executableName}",
                        writable: false);
                    var rawPath = key?.GetValue(null) as string;
                    if (string.IsNullOrWhiteSpace(rawPath))
                        continue;

                    var path = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
                    if (TryCreateEntry(appId, path, source, out entry))
                        return true;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    _logger.LogDebug(ex, "Could not read App Paths entry for {ExecutableName}", executableName);
                }
            }
        }

        return false;
    }

    private static bool TryResolveSystemDirectory(
        string appId,
        string executableName,
        out AppResolverCacheEntry entry) =>
        TryResolveSystemDirectory(appId, executableName, "system-directory", out entry);

    private static bool TryResolveSystemDirectory(
        string appId,
        string executableName,
        string source,
        out AppResolverCacheEntry entry)
    {
        entry = null!;
        var directories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        };

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            if (TryCreateEntry(appId, Path.Combine(directory, executableName), source, out entry))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> GetCommonCandidatePaths(string appId)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return appId switch
        {
            "chrome" or "google chrome" => NonEmptyPaths(
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")),
            "edge" or "microsoft edge" => NonEmptyPaths(
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe")),
            "firefox" => NonEmptyPaths(
                Path.Combine(programFiles, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(programFilesX86, "Mozilla Firefox", "firefox.exe")),
            "vscode" or "visual studio code" or "code" => NonEmptyPaths(
                Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"),
                Path.Combine(programFiles, "Microsoft VS Code", "Code.exe")),
            "obsidian" => NonEmptyPaths(
                Path.Combine(localAppData, "Programs", "Obsidian", "Obsidian.exe")),
            "postman" => NonEmptyPaths(
                Path.Combine(localAppData, "Postman", "Postman.exe"),
                Path.Combine(localAppData, "Programs", "Postman", "Postman.exe")),
            _ => []
        };
    }

    private bool TryResolveStartMenuShortcut(
        string appId,
        CancellationToken cancellationToken,
        out AppResolverCacheEntry entry)
    {
        entry = null!;
        var acceptedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { appId };
        if (BuiltInDisplayNames.TryGetValue(appId, out var displayNames))
            acceptedNames.UnionWith(displayNames);

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };
        var visited = 0;

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not enumerate Start Menu root {Root}", root);
                continue;
            }

            try
            {
                foreach (var shortcut in shortcuts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++visited > _options.MaxStartMenuShortcuts)
                        return false;

                    var stem = Path.GetFileNameWithoutExtension(shortcut);
                    if (!TryNormalizeAppId(stem, out var normalizedStem)
                        || !acceptedNames.Contains(normalizedStem))
                    {
                        continue;
                    }

                    var target = ShellLinkResolver.TryResolveTarget(shortcut);
                    if (target is not null
                        && TryCreateEntry(appId, target, "start-menu", out entry))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not finish enumerating Start Menu root {Root}", root);
            }
        }

        return false;
    }

    private static bool TryCreateEntry(
        string appId,
        string candidatePath,
        string source,
        out AppResolverCacheEntry entry)
    {
        entry = null!;
        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidatePath.Trim().Trim('"')));
            var info = new FileInfo(fullPath);
            if (!info.Exists
                || !string.Equals(info.Extension, ".exe", StringComparison.OrdinalIgnoreCase)
                || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || AppLauncher.IsBlockedExecutable(fullPath)
                || !AppLauncher.IsWindowsGuiExecutable(fullPath))
            {
                return false;
            }

            entry = new AppResolverCacheEntry
            {
                AppId = appId,
                ExecutablePath = fullPath,
                Source = source,
                FileLength = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                LastAccessedUtc = DateTimeOffset.UtcNow,
                RuntimeValidated = true
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> NonEmptyPaths(params string[] paths) =>
        paths.Where(path => !string.IsNullOrWhiteSpace(path));
}
