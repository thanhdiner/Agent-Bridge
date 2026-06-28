using System.Diagnostics;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.AppLaunch;

public sealed class AppOpener : IAppOpener
{
    private readonly IAppResolver _resolver;
    private readonly IAppLauncher _launcher;
    private readonly IUiAutomationExecutor _uiAutomationExecutor;

    public AppOpener(
        IAppResolver resolver,
        IAppLauncher launcher,
        IUiAutomationExecutor uiAutomationExecutor)
    {
        _resolver = resolver;
        _launcher = launcher;
        _uiAutomationExecutor = uiAutomationExecutor;
    }

    public async Task<CommandResult<AppOpenResult>> OpenAsync(
        string appId,
        IReadOnlyList<string> arguments,
        bool refresh,
        bool focusIfRunning,
        bool waitForWindow,
        string? windowTitleContains,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var target = AppOpenAliasCatalog.Resolve(appId, arguments);
        var effectiveWindowTitleContains = windowTitleContains ?? target.DefaultWindowTitleContains;
        var resolveResult = await _resolver.ResolveAsync(
            target.AppId,
            refresh,
            commandId,
            cancellationToken);

        if (!resolveResult.Success || resolveResult.Data is null)
        {
            return Failure(
                commandId,
                resolveResult.Error?.Code ?? ErrorCodes.AppResolveFailed,
                resolveResult.Error?.Message ?? "Application resolution failed.");
        }

        var resolved = resolveResult.Data;
        if (!resolved.Resolved)
        {
            return Failure(
                commandId,
                ErrorCodes.AppNotFound,
                $"Application '{resolved.NormalizedAppId}' could not be resolved on this device.");
        }

        if (string.IsNullOrWhiteSpace(resolved.ExecutablePath))
        {
            return Failure(
                commandId,
                ErrorCodes.InternalError,
                "Application resolution returned no executable path.");
        }

        if (focusIfRunning && target.Arguments.Count == 0)
        {
            var existing = await TryFocusExistingAsync(
                resolved.ExecutablePath,
                effectiveWindowTitleContains,
                commandId,
                cancellationToken);
            if (existing is not null)
            {
                var totalElapsedMs = stopwatch.ElapsedMilliseconds;
                return SuccessForExistingWindow(
                    appId,
                    target,
                    resolved,
                    existing,
                    totalElapsedMs,
                    commandId);
            }
        }

        var launchStartedAtMs = stopwatch.ElapsedMilliseconds;
        var launchResult = await _launcher.LaunchResolvedAsync(
            resolved.ExecutablePath,
            target.Arguments,
            waitForWindow,
            effectiveWindowTitleContains,
            timeoutMs,
            pollIntervalMs,
            commandId,
            cancellationToken);
        var totalElapsed = stopwatch.ElapsedMilliseconds;

        if (!launchResult.Success || launchResult.Data is null)
        {
            return Failure(
                commandId,
                launchResult.Error?.Code ?? ErrorCodes.AppLaunchFailed,
                launchResult.Error?.Message ?? "Application launch failed.");
        }

        return new CommandResult<AppOpenResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new AppOpenResult
            {
                AppId = appId,
                NormalizedAppId = resolved.NormalizedAppId,
                ResolvedAppId = target.AppId,
                AliasApplied = target.AliasApplied,
                Action = "launched",
                FocusedExisting = false,
                ExecutablePath = launchResult.Data.ExecutablePath,
                Source = resolved.Source,
                CacheHit = resolved.CacheHit,
                Refreshed = resolved.Refreshed,
                ResolveElapsedMs = resolved.ElapsedMs,
                LaunchElapsedMs = ToIntMilliseconds(totalElapsed - launchStartedAtMs),
                TotalElapsedMs = ToIntMilliseconds(totalElapsed),
                Launch = launchResult.Data
            }
        };
    }

    private async Task<ExistingWindowOutcome?> TryFocusExistingAsync(
        string executablePath,
        string? windowTitleContains,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        var listResult = await _uiAutomationExecutor.ListWindowsAsync(
            includeInvisible: false,
            includeUntitled: true,
            maxWindows: 500,
            commandId,
            cancellationToken);

        if (!listResult.Success || listResult.Data is null)
            return null;

        var window = listResult.Data.Windows
            .Where(candidate => string.Equals(
                NormalizeProcessName(candidate.ProcessName),
                NormalizeProcessName(processName),
                StringComparison.OrdinalIgnoreCase))
            .Where(candidate => windowTitleContains is null
                || candidate.Title.Contains(windowTitleContains, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.IsForeground)
            .ThenBy(candidate => candidate.IsMinimized)
            .ThenBy(candidate => candidate.ZOrder)
            .FirstOrDefault();

        if (window is null)
            return null;

        if (window.IsForeground)
            return new ExistingWindowOutcome(window, FocusRequested: false);

        var focusResult = await _uiAutomationExecutor.FocusWindowAsync(
            window.WindowHandle,
            commandId,
            cancellationToken);
        return focusResult.Success
            ? new ExistingWindowOutcome(window with { IsForeground = true, IsMinimized = false }, FocusRequested: true)
            : null;
    }

    private static CommandResult<AppOpenResult> SuccessForExistingWindow(
        string requestedAppId,
        AppOpenTarget target,
        AppResolveResult resolved,
        ExistingWindowOutcome existing,
        long totalElapsedMs,
        Guid commandId)
    {
        var syntheticLaunch = new AppLaunchResult
        {
            ExecutablePath = resolved.ExecutablePath!,
            ProcessName = existing.Window.ProcessName,
            ProcessId = existing.Window.ProcessId,
            Started = false,
            StartedAt = DateTimeOffset.MinValue,
            HasExited = false,
            WaitForWindow = false,
            WindowFound = true,
            WaitedMs = 0,
            PollCount = 0,
            Window = existing.Window
        };

        return new CommandResult<AppOpenResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new AppOpenResult
            {
                AppId = requestedAppId,
                NormalizedAppId = resolved.NormalizedAppId,
                ResolvedAppId = target.AppId,
                AliasApplied = target.AliasApplied,
                Action = existing.FocusRequested ? "focused-existing" : "already-foreground",
                FocusedExisting = true,
                ExecutablePath = resolved.ExecutablePath!,
                Source = resolved.Source,
                CacheHit = resolved.CacheHit,
                Refreshed = resolved.Refreshed,
                ResolveElapsedMs = resolved.ElapsedMs,
                LaunchElapsedMs = 0,
                TotalElapsedMs = ToIntMilliseconds(totalElapsedMs),
                Launch = syntheticLaunch
            }
        };
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

    private static CommandResult<AppOpenResult> Failure(
        Guid commandId,
        string code,
        string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };

    private sealed record ExistingWindowOutcome(
        WindowInfo Window,
        bool FocusRequested);
}
