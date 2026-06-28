using System.Diagnostics;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.AppLaunch;

public sealed class AppOpener : IAppOpener
{
    private readonly IAppResolver _resolver;
    private readonly IAppLauncher _launcher;

    public AppOpener(IAppResolver resolver, IAppLauncher launcher)
    {
        _resolver = resolver;
        _launcher = launcher;
    }

    public async Task<CommandResult<AppOpenResult>> OpenAsync(
        string appId,
        IReadOnlyList<string> arguments,
        bool refresh,
        bool waitForWindow,
        string? windowTitleContains,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var resolveResult = await _resolver.ResolveAsync(
            appId,
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

        var launchStartedAtMs = stopwatch.ElapsedMilliseconds;
        var launchResult = await _launcher.LaunchResolvedAsync(
            resolved.ExecutablePath,
            arguments,
            waitForWindow,
            windowTitleContains,
            timeoutMs,
            pollIntervalMs,
            commandId,
            cancellationToken);
        var totalElapsedMs = stopwatch.ElapsedMilliseconds;

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
                AppId = resolved.AppId,
                NormalizedAppId = resolved.NormalizedAppId,
                ExecutablePath = launchResult.Data.ExecutablePath,
                Source = resolved.Source,
                CacheHit = resolved.CacheHit,
                Refreshed = resolved.Refreshed,
                ResolveElapsedMs = resolved.ElapsedMs,
                LaunchElapsedMs = ToIntMilliseconds(totalElapsedMs - launchStartedAtMs),
                TotalElapsedMs = ToIntMilliseconds(totalElapsedMs),
                Launch = launchResult.Data
            }
        };
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
}
