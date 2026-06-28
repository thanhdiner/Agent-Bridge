using System.ComponentModel;
using System.Diagnostics;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.AppLaunch;

public sealed class AppCloser : IAppCloser
{
    private const int MaxTargets = 64;

    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "registry",
        "smss",
        "csrss",
        "wininit",
        "services",
        "lsass",
        "winlogon",
        "svchost",
        "fontdrvhost",
        "dwm"
    };

    private readonly IAppProcessCatalog _processCatalog;

    public AppCloser(IAppProcessCatalog processCatalog)
    {
        _processCatalog = processCatalog;
    }

    public async Task<CommandResult<AppCloseResult>> CloseAsync(
        int? processId,
        string? processName,
        bool allMatches,
        bool force,
        bool entireProcessTree,
        int timeoutMs,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var normalizedProcessName = NormalizeProcessName(processName);
        var validationError = Validate(processId, processName, normalizedProcessName, timeoutMs);
        if (validationError is not null)
            return Failure(commandId, validationError.Code, validationError.Message);

        IReadOnlyList<IAppProcess> targets;
        try
        {
            targets = ResolveTargets(processId, normalizedProcessName);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return Failure(commandId, ErrorCodes.AppCloseFailed, "Windows could not enumerate the requested application process.");
        }

        if (targets.Count == 0)
            return Failure(commandId, ErrorCodes.AppNotFound, "No matching application process is running.");

        if (targets.Count > MaxTargets)
        {
            DisposeAll(targets);
            return Failure(commandId, ErrorCodes.ResultLimitExceeded, $"The selector matched more than {MaxTargets} processes.");
        }

        if (processId.HasValue
            && normalizedProcessName is not null
            && !string.Equals(NormalizeProcessName(targets[0].Name), normalizedProcessName, StringComparison.OrdinalIgnoreCase))
        {
            DisposeAll(targets);
            return Failure(
                commandId,
                ErrorCodes.AppProcessMismatch,
                "The process id is live, but its process name does not match processName. The PID may have been reused.");
        }

        if (!processId.HasValue && targets.Count > 1 && !allMatches)
        {
            var processIds = string.Join(", ", targets.Take(10).Select(target => target.Id));
            DisposeAll(targets);
            return Failure(
                commandId,
                ErrorCodes.AppProcessAmbiguous,
                $"processName matched {targets.Count} processes (PIDs: {processIds}). Specify processId or set allMatches=true.");
        }

        var results = new List<AppCloseProcessResult>(targets.Count);
        try
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await CloseOneAsync(
                    target,
                    force,
                    entireProcessTree,
                    timeoutMs,
                    stopwatch,
                    cancellationToken));
            }
        }
        finally
        {
            DisposeAll(targets);
        }

        return new CommandResult<AppCloseResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new AppCloseResult
            {
                MatchedCount = targets.Count,
                CloseRequestedCount = results.Count(result => result.GracefulCloseRequested || result.ForceKillRequested),
                ClosedCount = results.Count(result => result.Closed),
                Force = force,
                EntireProcessTree = entireProcessTree,
                TimeoutMs = timeoutMs,
                ElapsedMs = ToElapsedMilliseconds(stopwatch.ElapsedMilliseconds),
                Processes = results
            }
        };
    }

    private IReadOnlyList<IAppProcess> ResolveTargets(int? processId, string? normalizedProcessName)
    {
        if (processId.HasValue)
        {
            var process = _processCatalog.GetById(processId.Value);
            return process is null ? [] : [process];
        }

        return _processCatalog.GetByName(normalizedProcessName!);
    }

    private async Task<AppCloseProcessResult> CloseOneAsync(
        IAppProcess process,
        bool force,
        bool entireProcessTree,
        int timeoutMs,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var processId = process.Id;
        var processName = SafeReadName(process);

        if (processId == _processCatalog.CurrentProcessId || ProtectedProcessNames.Contains(processName))
        {
            return ProcessFailure(
                processId,
                processName,
                ErrorCodes.AppCloseProtected,
                "Refusing to close the agent itself or a protected Windows process.");
        }

        try
        {
            if (process.HasExited)
                return ProcessSuccess(processId, processName, closed: true);

            var gracefulRequested = process.CloseMainWindow();
            var remainingMs = RemainingMilliseconds(timeoutMs, stopwatch.ElapsedMilliseconds);
            var gracefulWaitMs = force ? Math.Min(1_000, remainingMs) : remainingMs;
            var closed = gracefulRequested
                && await process.WaitForExitAsync(gracefulWaitMs, cancellationToken);

            if (closed)
            {
                return ProcessSuccess(
                    processId,
                    processName,
                    closed: true,
                    gracefulRequested: true);
            }

            if (!force)
            {
                return new AppCloseProcessResult
                {
                    ProcessId = processId,
                    ProcessName = processName,
                    GracefulCloseRequested = gracefulRequested,
                    Closed = false,
                    TimedOut = gracefulRequested && RemainingMilliseconds(timeoutMs, stopwatch.ElapsedMilliseconds) == 0,
                    ErrorCode = ErrorCodes.AppCloseFailed,
                    ErrorMessage = gracefulRequested
                        ? "The process did not exit before timeout. It may be showing an unsaved-work prompt."
                        : "The process has no closable main window. Retry with force=true to terminate it."
                };
            }

            process.Kill(entireProcessTree);
            remainingMs = RemainingMilliseconds(timeoutMs, stopwatch.ElapsedMilliseconds);
            closed = await process.WaitForExitAsync(remainingMs, cancellationToken);

            return new AppCloseProcessResult
            {
                ProcessId = processId,
                ProcessName = processName,
                GracefulCloseRequested = gracefulRequested,
                ForceKillRequested = true,
                Closed = closed,
                TimedOut = !closed && remainingMs == 0,
                ErrorCode = closed ? null : ErrorCodes.AppCloseFailed,
                ErrorMessage = closed ? null : "The process did not exit before timeout after force kill was requested."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            return ProcessFailure(
                processId,
                processName,
                ErrorCodes.AccessDenied,
                "Windows denied permission to close the process.");
        }
        catch (InvalidOperationException)
        {
            return new AppCloseProcessResult
            {
                ProcessId = processId,
                ProcessName = processName,
                Closed = SafeHasExited(process),
                ErrorCode = SafeHasExited(process) ? null : ErrorCodes.AppCloseFailed,
                ErrorMessage = SafeHasExited(process) ? null : "The process became unavailable while the close request was running."
            };
        }
        catch (Exception)
        {
            return ProcessFailure(
                processId,
                processName,
                ErrorCodes.AppCloseFailed,
                "An unexpected error occurred while closing the process.");
        }
    }

    private static CommandError? Validate(
        int? processId,
        string? processName,
        string? normalizedProcessName,
        int timeoutMs)
    {
        if (!processId.HasValue && normalizedProcessName is null)
            return new CommandError(ErrorCodes.InvalidRequest, "At least one of processId or processName is required.");
        if (processId is <= 0)
            return new CommandError(ErrorCodes.InvalidRequest, "processId must be greater than zero.");
        if (processName is not null && (processName.Length > 128 || processName.Any(char.IsControl)))
            return new CommandError(ErrorCodes.InvalidRequest, "processName must be at most 128 characters without control characters.");
        if (timeoutMs is < 1 or > 300_000)
            return new CommandError(ErrorCodes.InvalidRequest, "timeoutMs must be between 1 and 300000.");

        return null;
    }

    private static string? NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return null;

        var normalized = processName.Trim();
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private static string SafeReadName(IAppProcess process)
    {
        try
        {
            return NormalizeProcessName(process.Name) ?? string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static bool SafeHasExited(IAppProcess process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static int RemainingMilliseconds(int timeoutMs, long elapsedMs) =>
        (int)Math.Max(0, timeoutMs - Math.Min(timeoutMs, elapsedMs));

    private static int ToElapsedMilliseconds(long elapsedMs) =>
        (int)Math.Min(int.MaxValue, Math.Max(0, elapsedMs));

    private static AppCloseProcessResult ProcessSuccess(
        int processId,
        string processName,
        bool closed,
        bool gracefulRequested = false) =>
        new()
        {
            ProcessId = processId,
            ProcessName = processName,
            GracefulCloseRequested = gracefulRequested,
            Closed = closed
        };

    private static AppCloseProcessResult ProcessFailure(
        int processId,
        string processName,
        string errorCode,
        string errorMessage) =>
        new()
        {
            ProcessId = processId,
            ProcessName = processName,
            Closed = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };

    private static void DisposeAll(IEnumerable<IAppProcess> processes)
    {
        foreach (var process in processes)
            process.Dispose();
    }

    private static CommandResult<AppCloseResult> Failure(Guid commandId, string code, string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };
}
