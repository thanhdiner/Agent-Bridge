using System.Diagnostics;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.AppLaunch;

public sealed class ProcessWaiter : IProcessWaiter
{
    private const int MaxTimeoutMs = 300_000;
    private const int MinPollIntervalMs = 25;
    private const int MaxPollIntervalMs = 5_000;
    private const int MaxProcessNameCharacters = 260;

    private readonly IAppProcessCatalog _processCatalog;

    public ProcessWaiter(IAppProcessCatalog processCatalog)
    {
        _processCatalog = processCatalog;
    }

    public async Task<CommandResult<ProcessWaitResult>> WaitAsync(
        int? processId,
        string? processName,
        int occurrenceIndex,
        string condition,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var normalizedProcessName = NormalizeProcessName(processName);
        if (!processId.HasValue && normalizedProcessName is null)
            return Failure(commandId, ErrorCodes.InvalidRequest, "processId or processName is required.");
        if (processId is <= 0)
            return Failure(commandId, ErrorCodes.InvalidRequest, "processId must be greater than zero.");
        if (processName is not null
            && (normalizedProcessName is null
                || processName.Length > MaxProcessNameCharacters
                || processName.Any(char.IsControl)))
        {
            return Failure(commandId, ErrorCodes.InvalidRequest, "processName is invalid.");
        }
        if (occurrenceIndex is < 0 or > 1000)
            return Failure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (!ProcessWaitConditions.TryNormalize(condition, out var normalizedCondition))
            return Failure(commandId, ErrorCodes.InvalidRequest, "condition must be exists, not-exists, appears, disappears, or exited.");
        if (timeoutMs is < 1 or > MaxTimeoutMs)
            return Failure(commandId, ErrorCodes.InvalidRequest, $"timeoutMs must be between 1 and {MaxTimeoutMs}.");
        if (pollIntervalMs is < MinPollIntervalMs or > MaxPollIntervalMs)
            return Failure(commandId, ErrorCodes.InvalidRequest, $"pollIntervalMs must be between {MinPollIntervalMs} and {MaxPollIntervalMs}.");

        var stopwatch = Stopwatch.StartNew();
        var pollCount = 0;
        var lastObservation = ProcessObservation.NotFound;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastObservation = CaptureObservation(processId, normalizedProcessName, occurrenceIndex);
                pollCount++;

                var satisfied = normalizedCondition == ProcessWaitConditions.Exists
                    ? lastObservation.ProcessFound
                    : !lastObservation.ProcessFound;
                if (satisfied)
                {
                    var elapsedMs = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
                    return new CommandResult<ProcessWaitResult>
                    {
                        CommandId = commandId,
                        Success = true,
                        Data = new ProcessWaitResult
                        {
                            Condition = normalizedCondition,
                            CompletionReason = "condition-satisfied",
                            FinalState = lastObservation.ProcessFound ? "exists" : "not-exists",
                            ElapsedMs = elapsedMs,
                            WaitedMs = elapsedMs,
                            PollCount = pollCount,
                            OccurrenceIndex = occurrenceIndex,
                            ProcessFound = lastObservation.ProcessFound,
                            ProcessId = lastObservation.ProcessId,
                            ProcessName = lastObservation.ProcessName
                        }
                    };
                }

                var elapsed = stopwatch.ElapsedMilliseconds;
                if (elapsed >= timeoutMs)
                    break;

                var delayMs = (int)Math.Min(pollIntervalMs, timeoutMs - elapsed);
                await Task.Delay(delayMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return Failure(commandId, ErrorCodes.CommandCancelled, "The process wait request was cancelled.");
        }
        catch (Exception)
        {
            return Failure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while waiting for the process.");
        }

        var finalState = lastObservation.ProcessFound
            ? $"Process {lastObservation.ProcessName} ({lastObservation.ProcessId}) still exists."
            : "No matching live process was found on the final poll.";
        return Failure(
            commandId,
            ErrorCodes.ProcessWaitTimeout,
            $"Condition '{normalizedCondition}' was not satisfied within {timeoutMs} ms after {pollCount} polls. {finalState}");
    }

    private ProcessObservation CaptureObservation(
        int? processId,
        string? normalizedProcessName,
        int occurrenceIndex)
    {
        if (processId.HasValue)
        {
            using var process = _processCatalog.GetById(processId.Value);
            if (process is null || SafeHasExited(process))
                return ProcessObservation.NotFound;

            var name = SafeName(process);
            if (normalizedProcessName is not null
                && !string.Equals(NormalizeProcessName(name), normalizedProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return ProcessObservation.NotFound;
            }

            return new ProcessObservation(true, process.Id, name);
        }

        var processes = _processCatalog.GetByName(normalizedProcessName!);
        try
        {
            var liveIndex = 0;
            foreach (var process in processes)
            {
                if (SafeHasExited(process))
                    continue;
                if (liveIndex++ != occurrenceIndex)
                    continue;

                return new ProcessObservation(true, process.Id, SafeName(process));
            }

            return ProcessObservation.NotFound;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static bool SafeHasExited(IAppProcess process)
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

    private static string SafeName(IAppProcess process)
    {
        try
        {
            return process.Name;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    internal static string? NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return null;

        var normalized = processName.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static CommandResult<ProcessWaitResult> Failure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };

    private sealed record ProcessObservation(bool ProcessFound, int? ProcessId, string? ProcessName)
    {
        public static readonly ProcessObservation NotFound = new(false, null, null);
    }
}
