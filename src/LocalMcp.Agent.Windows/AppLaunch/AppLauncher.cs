using System.ComponentModel;
using System.Diagnostics;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalMcp.Agent.Windows.AppLaunch;

public sealed class AppLauncher : IAppLauncher
{
    private const int MaxArguments = 64;
    private const int MaxArgumentCharacters = 4096;
    private const int MaxTotalArgumentCharacters = 32_768;
    private const int MaxTimeoutMs = 300_000;
    private const int MinPollIntervalMs = 25;
    private const int MaxPollIntervalMs = 5_000;
    private const ushort ImageSubsystemWindowsGui = 2;

    private static readonly HashSet<string> BlockedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "wscript.exe",
        "cscript.exe",
        "mshta.exe",
        "rundll32.exe",
        "regsvr32.exe",
        "msiexec.exe",
        "installutil.exe",
        "regasm.exe",
        "regsvcs.exe",
        "wmic.exe",
        "wsl.exe",
        "bash.exe",
        "python.exe",
        "pythonw.exe",
        "node.exe",
        "java.exe",
        "javaw.exe",
        "ruby.exe",
        "perl.exe"
    };

    private readonly IPathPolicy _pathPolicy;
    private readonly IUiAutomationExecutor _uiAutomationExecutor;
    private readonly AppLaunchOptions _options;
    private readonly ILogger<AppLauncher> _logger;

    public AppLauncher(
        IPathPolicy pathPolicy,
        IUiAutomationExecutor uiAutomationExecutor,
        IOptions<AppLaunchOptions> options,
        ILogger<AppLauncher> logger)
    {
        _pathPolicy = pathPolicy;
        _uiAutomationExecutor = uiAutomationExecutor;
        _options = options.Value;
        _logger = logger;
    }

    public Task<CommandResult<AppLaunchResult>> LaunchAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        bool waitForWindow,
        string? windowTitleContains,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken) =>
        LaunchCoreAsync(
            executable,
            arguments,
            workingDirectory,
            waitForWindow,
            windowTitleContains,
            timeoutMs,
            pollIntervalMs,
            resolvedExecutable: false,
            commandId,
            cancellationToken);

    public Task<CommandResult<AppLaunchResult>> LaunchResolvedAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        bool waitForWindow,
        string? windowTitleContains,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken) =>
        LaunchCoreAsync(
            executablePath,
            arguments,
            workingDirectory: null,
            waitForWindow,
            windowTitleContains,
            timeoutMs,
            pollIntervalMs,
            resolvedExecutable: true,
            commandId,
            cancellationToken);

    private async Task<CommandResult<AppLaunchResult>> LaunchCoreAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        bool waitForWindow,
        string? windowTitleContains,
        int timeoutMs,
        int pollIntervalMs,
        bool resolvedExecutable,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executable)
            || executable.Length > 32_768
            || executable.Any(char.IsControl))
        {
            return Failure(commandId, ErrorCodes.InvalidRequest,
                "executable must be non-empty, contain no control characters, and be at most 32768 characters.");
        }

        arguments ??= [];
        if (arguments.Count > MaxArguments)
            return Failure(commandId, ErrorCodes.InvalidRequest, $"arguments may contain at most {MaxArguments} entries.");

        var totalArgumentCharacters = 0;
        foreach (var argument in arguments)
        {
            if (argument is null
                || argument.Length > MaxArgumentCharacters
                || argument.Any(character => character == '\0'))
            {
                return Failure(commandId, ErrorCodes.InvalidRequest,
                    $"Each argument must be non-null, contain no NUL characters, and be at most {MaxArgumentCharacters} characters.");
            }

            totalArgumentCharacters = checked(totalArgumentCharacters + argument.Length);
            if (totalArgumentCharacters > MaxTotalArgumentCharacters)
                return Failure(commandId, ErrorCodes.InvalidRequest,
                    $"The combined argument length must not exceed {MaxTotalArgumentCharacters} characters.");
        }

        if (windowTitleContains is not null
            && (windowTitleContains.Length > 1024 || windowTitleContains.Any(char.IsControl)))
        {
            return Failure(commandId, ErrorCodes.InvalidRequest,
                "windowTitleContains must contain no control characters and be at most 1024 characters.");
        }

        if (timeoutMs is < 1 or > MaxTimeoutMs)
            return Failure(commandId, ErrorCodes.InvalidRequest, $"timeoutMs must be between 1 and {MaxTimeoutMs}.");
        if (pollIntervalMs is < MinPollIntervalMs or > MaxPollIntervalMs)
            return Failure(commandId, ErrorCodes.InvalidRequest,
                $"pollIntervalMs must be between {MinPollIntervalMs} and {MaxPollIntervalMs}.");
        if (!OperatingSystem.IsWindows())
            return Failure(commandId, ErrorCodes.AppLaunchFailed, "Application launch is only available on Windows agents.");
        if (FileSystemExecutor.IsCurrentProcessElevated())
            return Failure(commandId, ErrorCodes.AccessDenied,
                "Application launch is disabled while the Windows agent is running elevated.");

        string executablePath;
        var resolveError = resolvedExecutable
            ? ValidateExplicitExecutable(executable, out executablePath)
            : ResolveExecutable(executable, out executablePath);
        if (resolveError is not null)
            return new CommandResult<AppLaunchResult>
            {
                CommandId = commandId,
                Success = false,
                Error = resolveError
            };

        if (IsBlockedExecutable(executablePath))
            return Failure(commandId, ErrorCodes.AppExecutableNotAllowed,
                "The requested executable is a command, script, installer, or code host and cannot be launched by app_launch.");
        if (!IsWindowsGuiExecutable(executablePath))
            return Failure(commandId, ErrorCodes.AppExecutableInvalid,
                "The requested executable is not a Windows GUI application.");

        string normalizedWorkingDirectory;
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            normalizedWorkingDirectory = Path.GetDirectoryName(executablePath)!;
        }
        else
        {
            var directoryError = _pathPolicy.AuthorizeReadDirectory(
                workingDirectory,
                out normalizedWorkingDirectory);
            if (directoryError is not null)
            {
                return new CommandResult<AppLaunchResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = directoryError
                };
            }
        }

        WindowListResult? beforeWindows = null;
        if (waitForWindow)
        {
            var beforeResult = await _uiAutomationExecutor.ListWindowsAsync(
                includeInvisible: false,
                includeUntitled: true,
                maxWindows: 500,
                commandId,
                cancellationToken);
            if (!beforeResult.Success || beforeResult.Data is null)
            {
                return Failure(commandId,
                    beforeResult.Error?.Code ?? ErrorCodes.WindowEnumerationFailed,
                    beforeResult.Error?.Message ?? "Windows could not enumerate windows before application launch.");
            }

            beforeWindows = beforeResult.Data;
        }

        var startInfo = CreateStartInfo(executablePath, normalizedWorkingDirectory, arguments);
        Process? process = null;
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return Failure(commandId, ErrorCodes.AppLaunchFailed, "The application process could not be started.");

            var processId = process.Id;
            var processName = SafeReadProcessName(process, executablePath);
            var waitOutcome = waitForWindow
                ? await WaitForLaunchedWindowAsync(
                    processId,
                    processName,
                    windowTitleContains,
                    beforeWindows!,
                    timeoutMs,
                    pollIntervalMs,
                    commandId,
                    cancellationToken)
                : AppWindowWaitOutcome.NotRequested;

            var hasExited = SafeHasExited(process);
            return new CommandResult<AppLaunchResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new AppLaunchResult
                {
                    ExecutablePath = executablePath,
                    ProcessName = processName,
                    ProcessId = processId,
                    Started = true,
                    StartedAt = startedAt,
                    HasExited = hasExited,
                    ExitCode = hasExited ? SafeExitCode(process) : null,
                    WaitForWindow = waitForWindow,
                    WindowFound = waitOutcome.Window is not null,
                    WindowWaitTimedOut = waitOutcome.TimedOut,
                    WindowWaitErrorCode = waitOutcome.ErrorCode,
                    WindowWaitErrorMessage = waitOutcome.ErrorMessage,
                    WaitedMs = waitOutcome.WaitedMs,
                    PollCount = waitOutcome.PollCount,
                    Window = waitOutcome.Window
                }
            };
        }
        catch (OperationCanceledException)
        {
            return Failure(commandId, ErrorCodes.CommandCancelled,
                "The application launch request was cancelled. A process that already started may still be running.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Application launch failed for {ExecutablePath}", executablePath);
            return Failure(commandId, ErrorCodes.AppLaunchFailed,
                "Windows could not start the requested application.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected application launch failure for command {CommandId}", commandId);
            return Failure(commandId, ErrorCodes.InternalError,
                "An unexpected error occurred while launching the application.");
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal ProcessStartInfo CreateStartInfo(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        foreach (var variableName in startInfo.Environment.Keys.ToArray())
        {
            if (FileSystemExecutor.IsSensitiveEnvironmentVariable(variableName))
                startInfo.Environment.Remove(variableName);
        }

        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        return startInfo;
    }

    internal CommandError? ResolveExecutable(string requestedExecutable, out string executablePath)
    {
        executablePath = string.Empty;
        string requested;
        string fileName;
        try
        {
            requested = requestedExecutable.Trim();
            fileName = Path.GetFileName(requested);
        }
        catch
        {
            return new CommandError(ErrorCodes.InvalidPath, "The executable path format is invalid.");
        }
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(Path.GetExtension(fileName), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new CommandError(ErrorCodes.AppExecutableInvalid, "Only .exe applications can be launched.");
        }

        if (Path.IsPathRooted(requested))
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(requested);
            }
            catch
            {
                return new CommandError(ErrorCodes.InvalidPath, "The executable path format is invalid.");
            }

            if (IsExplicitlyAllowedPath(fullPath))
                return ValidateExplicitExecutable(fullPath, out executablePath);

            var policyError = _pathPolicy.AuthorizeExecuteFile(fullPath, out executablePath);
            return policyError;
        }

        if (requested.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return new CommandError(ErrorCodes.AppExecutableInvalid,
                "Relative executable paths are not supported. Use an absolute path or an allowlisted executable name.");
        if (!IsExplicitlyAllowedName(fileName))
            return new CommandError(ErrorCodes.AppExecutableNotAllowed,
                "The executable name is not present in AppLaunch:AllowedExecutables.");

        var resolved = ResolveSystemExecutable(fileName);
        if (resolved is null)
            return new CommandError(ErrorCodes.FileNotFound, "The allowlisted executable was not found in a Windows system directory.");

        return ValidateExplicitExecutable(resolved, out executablePath);
    }

    internal static bool IsBlockedExecutableName(string executableName)
    {
        try
        {
            return BlockedExecutables.Contains(Path.GetFileName(executableName));
        }
        catch
        {
            return true;
        }
    }

    internal static bool IsBlockedExecutable(string executablePath)
    {
        if (IsBlockedExecutableName(executablePath))
            return true;

        try
        {
            var version = FileVersionInfo.GetVersionInfo(executablePath);
            return IsBlockedExecutableName(version.OriginalFilename ?? string.Empty)
                || IsBlockedExecutableName(version.InternalName ?? string.Empty);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsWindowsGuiExecutable(string executablePath)
    {
        try
        {
            using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40)
                return false;

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 96)
                return false;

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
                return false;

            stream.Position = peOffset + 24;
            var optionalMagic = reader.ReadUInt16();
            if (optionalMagic is not 0x10B and not 0x20B)
                return false;

            stream.Position = peOffset + 24 + 68;
            return reader.ReadUInt16() == ImageSubsystemWindowsGui;
        }
        catch
        {
            return false;
        }
    }

    private async Task<AppWindowWaitOutcome> WaitForLaunchedWindowAsync(
        int processId,
        string processName,
        string? titleContains,
        WindowListResult beforeWindows,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var beforeByHandle = beforeWindows.Windows.ToDictionary(
            window => window.WindowHandle,
            window => window.Title,
            StringComparer.OrdinalIgnoreCase);
        var stopwatch = Stopwatch.StartNew();
        var pollCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listResult = await _uiAutomationExecutor.ListWindowsAsync(
                includeInvisible: false,
                includeUntitled: true,
                maxWindows: 500,
                commandId,
                cancellationToken);
            pollCount++;

            if (!listResult.Success || listResult.Data is null)
            {
                return new AppWindowWaitOutcome(
                    Window: null,
                    TimedOut: false,
                    WaitedMs: ToIntMilliseconds(stopwatch.ElapsedMilliseconds),
                    PollCount: pollCount,
                    ErrorCode: listResult.Error?.Code ?? ErrorCodes.WindowEnumerationFailed,
                    ErrorMessage: listResult.Error?.Message ?? "Windows could not enumerate application windows.");
            }

            var window = listResult.Data.Windows
                .Where(candidate => MatchesLaunchedApplication(
                    candidate,
                    processId,
                    processName,
                    titleContains))
                .Where(candidate =>
                    !beforeByHandle.TryGetValue(candidate.WindowHandle, out var priorTitle)
                    || !string.Equals(priorTitle, candidate.Title, StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.ProcessId == processId)
                .ThenByDescending(candidate => candidate.IsForeground)
                .ThenBy(candidate => candidate.ZOrder)
                .FirstOrDefault();

            if (window is not null)
            {
                return new AppWindowWaitOutcome(
                    window,
                    TimedOut: false,
                    ToIntMilliseconds(stopwatch.ElapsedMilliseconds),
                    pollCount,
                    ErrorCode: null,
                    ErrorMessage: null);
            }

            var elapsedMs = stopwatch.ElapsedMilliseconds;
            if (elapsedMs >= timeoutMs)
            {
                return new AppWindowWaitOutcome(
                    Window: null,
                    TimedOut: true,
                    WaitedMs: ToIntMilliseconds(elapsedMs),
                    PollCount: pollCount,
                    ErrorCode: ErrorCodes.WindowWaitTimeout,
                    ErrorMessage: $"No matching application window appeared within {timeoutMs} ms.");
            }

            await Task.Delay((int)Math.Min(pollIntervalMs, timeoutMs - elapsedMs), cancellationToken);
        }
    }

    private static bool MatchesLaunchedApplication(
        WindowInfo window,
        int processId,
        string processName,
        string? titleContains)
    {
        if (titleContains is not null
            && !window.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (window.ProcessId == processId)
            return true;

        if (string.Equals(
            NormalizeProcessName(window.ProcessName),
            NormalizeProcessName(processName),
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return titleContains is not null;
    }

    private CommandError? ValidateExplicitExecutable(string path, out string executablePath)
    {
        executablePath = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return new CommandError(ErrorCodes.FileNotFound, "The executable file was not found.");
            if (Directory.Exists(fullPath))
                return new CommandError(ErrorCodes.AccessDenied, "The executable path is a directory, not a file.");
            if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
                return new CommandError(ErrorCodes.AppExecutableInvalid, "Only .exe applications can be launched.");
            if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
                return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed for executable paths.");

            executablePath = fullPath;
            return null;
        }
        catch
        {
            return new CommandError(ErrorCodes.AccessDenied, "The executable path could not be validated.");
        }
    }

    private bool IsExplicitlyAllowedName(string fileName) =>
        _options.AllowedExecutables.Any(entry =>
            !Path.IsPathRooted(entry)
            && string.Equals(entry.Trim(), fileName, StringComparison.OrdinalIgnoreCase));

    private bool IsExplicitlyAllowedPath(string fullPath) =>
        _options.AllowedExecutables.Any(entry =>
        {
            if (!Path.IsPathRooted(entry))
                return false;
            try
            {
                return string.Equals(Path.GetFullPath(entry), fullPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });

    private static string? ResolveSystemExecutable(string fileName)
    {
        var candidates = new List<string>();
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(systemDirectory))
            candidates.Add(Path.Combine(systemDirectory, fileName));

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
            candidates.Add(Path.Combine(windowsDirectory, fileName));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string SafeReadProcessName(Process process, string executablePath)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return Path.GetFileNameWithoutExtension(executablePath);
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeProcessName(string value)
    {
        var normalized = value.Trim();
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private static int ToIntMilliseconds(long value) =>
        (int)Math.Min(int.MaxValue, Math.Max(0, value));

    private static CommandResult<AppLaunchResult> Failure(
        Guid commandId,
        string code,
        string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };

    private sealed record AppWindowWaitOutcome(
        WindowInfo? Window,
        bool TimedOut,
        int WaitedMs,
        int PollCount,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static readonly AppWindowWaitOutcome NotRequested = new(
            Window: null,
            TimedOut: false,
            WaitedMs: 0,
            PollCount: 0,
            ErrorCode: null,
            ErrorMessage: null);
    }
}
