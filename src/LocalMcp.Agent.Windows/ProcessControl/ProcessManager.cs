using System.ComponentModel;
using System.Diagnostics;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.ProcessControl;

public sealed class ProcessManager : IProcessManager
{
    private const int MaxNameCharacters = 260;
    private const int MaxResultsLimit = 1_000;
    private const int MaxTimeoutMs = 300_000;

    public Task<CommandResult<ProcessListResult>> ListAsync(
        string? nameContains,
        bool includeWindowless,
        int maxResults,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (nameContains is not null
            && (nameContains.Length > MaxNameCharacters || nameContains.Any(char.IsControl)))
        {
            return Task.FromResult(Failure<ProcessListResult>(
                commandId,
                ErrorCodes.InvalidRequest,
                $"nameContains must be at most {MaxNameCharacters} characters without control characters."));
        }
        if (maxResults is < 1 or > MaxResultsLimit)
        {
            return Task.FromResult(Failure<ProcessListResult>(
                commandId,
                ErrorCodes.InvalidRequest,
                $"maxResults must be between 1 and {MaxResultsLimit}."));
        }

        return Task.Run(
            () => ListProcesses(nameContains?.Trim(), includeWindowless, maxResults, commandId, cancellationToken),
            cancellationToken);
    }

    public async Task<CommandResult<ProcessKillResult>> KillAsync(
        int processId,
        string? expectedProcessName,
        bool entireProcessTree,
        int timeoutMs,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (processId <= 0)
            return Failure<ProcessKillResult>(commandId, ErrorCodes.InvalidRequest, "processId must be greater than zero.");
        if (expectedProcessName is not null
            && (string.IsNullOrWhiteSpace(expectedProcessName)
                || expectedProcessName.Length > MaxNameCharacters
                || expectedProcessName.Any(char.IsControl)))
        {
            return Failure<ProcessKillResult>(commandId, ErrorCodes.InvalidRequest, "expectedProcessName is invalid.");
        }
        if (timeoutMs is < 1 or > MaxTimeoutMs)
            return Failure<ProcessKillResult>(commandId, ErrorCodes.InvalidRequest, $"timeoutMs must be between 1 and {MaxTimeoutMs}.");

        Process? process = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            process = Process.GetProcessById(processId);
            string processName;
            try
            {
                processName = process.ProcessName;
            }
            catch (InvalidOperationException)
            {
                return Failure<ProcessKillResult>(commandId, ErrorCodes.ProcessNotFound, "The requested process is no longer running.");
            }

            var normalizedName = ProcessProtection.NormalizeName(processName);
            var expectedName = ProcessProtection.NormalizeName(expectedProcessName);
            if (expectedName.Length > 0
                && !string.Equals(normalizedName, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return Failure<ProcessKillResult>(
                    commandId,
                    ErrorCodes.ProcessMismatch,
                    "The live process name does not match expectedProcessName. The PID may have been reused.");
            }

            if (ProcessProtection.IsProtected(processId, normalizedName, Environment.ProcessId))
            {
                return Failure<ProcessKillResult>(
                    commandId,
                    ErrorCodes.ProcessKillProtected,
                    "Refusing to terminate the agent itself or a protected Windows process.");
            }

            if (SafeHasExited(process))
            {
                return Success(commandId, new ProcessKillResult
                {
                    ProcessId = processId,
                    ProcessName = normalizedName,
                    EntireProcessTree = entireProcessTree,
                    KillRequested = false,
                    Exited = true,
                    TimeoutMs = timeoutMs,
                    ElapsedMs = ToElapsedMilliseconds(stopwatch.ElapsedMilliseconds)
                });
            }

            process.Kill(entireProcessTree);
            var exited = await WaitForExitAsync(process, timeoutMs, cancellationToken);
            if (!exited)
            {
                return Failure<ProcessKillResult>(
                    commandId,
                    ErrorCodes.ProcessKillFailed,
                    "Windows accepted the kill request, but the process did not exit before timeout.");
            }

            return Success(commandId, new ProcessKillResult
            {
                ProcessId = processId,
                ProcessName = normalizedName,
                EntireProcessTree = entireProcessTree,
                KillRequested = true,
                Exited = true,
                TimeoutMs = timeoutMs,
                ElapsedMs = ToElapsedMilliseconds(stopwatch.ElapsedMilliseconds)
            });
        }
        catch (ArgumentException)
        {
            return Failure<ProcessKillResult>(commandId, ErrorCodes.ProcessNotFound, "No live process has the requested processId.");
        }
        catch (OperationCanceledException)
        {
            return Failure<ProcessKillResult>(commandId, ErrorCodes.CommandCancelled, "The process kill request was cancelled.");
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            return Failure<ProcessKillResult>(commandId, ErrorCodes.AccessDenied, "Windows denied permission to terminate the process.");
        }
        catch (InvalidOperationException)
        {
            return Failure<ProcessKillResult>(commandId, ErrorCodes.ProcessNotFound, "The requested process exited while the kill request was running.");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static CommandResult<ProcessListResult> ListProcesses(
        string? nameContains,
        bool includeWindowless,
        int maxResults,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var items = new List<ProcessListItem>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = TryReadProcess(process);
                if (item is null)
                    continue;
                if (!string.IsNullOrEmpty(nameContains)
                    && !item.ProcessName.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!includeWindowless && item.MainWindowHandle is null)
                    continue;
                items.Add(item);
            }
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }

        var ordered = items
            .OrderBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProcessId)
            .ToArray();
        var truncated = ordered.Length > maxResults;
        var returned = truncated ? ordered[..maxResults] : ordered;

        return Success(commandId, new ProcessListResult
        {
            Count = returned.Length,
            Truncated = truncated,
            Processes = returned
        });
    }

    private static ProcessListItem? TryReadProcess(Process process)
    {
        try
        {
            if (process.HasExited)
                return null;

            var name = process.ProcessName;
            var windowHandle = SafeRead(() => process.MainWindowHandle, IntPtr.Zero);
            var title = windowHandle == IntPtr.Zero
                ? null
                : Limit(SafeRead(() => process.MainWindowTitle, string.Empty), 1_024);

            return new ProcessListItem
            {
                ProcessId = process.Id,
                ProcessName = name,
                SessionId = SafeReadNullable(() => process.SessionId),
                MainWindowHandle = windowHandle == IntPtr.Zero ? null : $"0x{unchecked((ulong)windowHandle.ToInt64()):X}",
                MainWindowTitle = string.IsNullOrEmpty(title) ? null : title,
                Responding = windowHandle == IntPtr.Zero ? null : SafeReadNullable(() => process.Responding),
                StartTimeUtc = SafeReadNullable(() => new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero)),
                WorkingSetBytes = SafeReadNullable(() => process.WorkingSet64)
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs, CancellationToken cancellationToken)
    {
        if (SafeHasExited(process))
            return true;

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(
                TimeSpan.FromMilliseconds(timeoutMs),
                cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return SafeHasExited(process);
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static T SafeRead<T>(Func<T> reader, T fallback)
    {
        try
        {
            return reader();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return fallback;
        }
    }

    private static T? SafeReadNullable<T>(Func<T> reader) where T : struct
    {
        try
        {
            return reader();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static string Limit(string value, int maxCharacters) =>
        value.Length <= maxCharacters ? value : value[..maxCharacters];

    private static int ToElapsedMilliseconds(long elapsedMs) =>
        (int)Math.Min(int.MaxValue, Math.Max(0, elapsedMs));

    private static CommandResult<T> Success<T>(Guid commandId, T data) => new()
    {
        CommandId = commandId,
        Success = true,
        Data = data
    };

    private static CommandResult<T> Failure<T>(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };
}
