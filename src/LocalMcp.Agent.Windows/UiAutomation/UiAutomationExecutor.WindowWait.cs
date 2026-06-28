using System.Diagnostics;
using System.Runtime.Versioning;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const int MaxWindowWaitTimeoutMs = 300_000;
    private const int MinWindowWaitPollIntervalMs = 25;
    private const int MaxWindowWaitPollIntervalMs = 5_000;
    private const int MaxWindowSelectorCharacters = 1024;

    public async Task<CommandResult<WindowWaitResult>> WaitForWindowAsync(
        string? windowHandle,
        int? processId,
        string? processName,
        string? className,
        string? title,
        string? titleContains,
        int occurrenceIndex,
        string condition,
        string? expectedTitle,
        bool includeInvisible,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var hasWindowHandle = !string.IsNullOrWhiteSpace(windowHandle);
        var hasSelector = hasWindowHandle
            || processId.HasValue
            || !string.IsNullOrWhiteSpace(processName)
            || !string.IsNullOrWhiteSpace(className)
            || title is not null
            || titleContains is not null;
        if (!hasSelector)
            return WindowWaitFailure(commandId, ErrorCodes.InvalidRequest, "At least one window selector is required.");

        IntPtr parsedHandle = IntPtr.Zero;
        if (hasWindowHandle && !TryParseWindowHandle(windowHandle, out parsedHandle))
            return WindowWaitFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (processId is <= 0)
            return WindowWaitFailure(commandId, ErrorCodes.InvalidRequest, "processId must be greater than zero.");
        if (!ValidWindowSelectorText(processName)
            || !ValidWindowSelectorText(className)
            || !ValidWindowSelectorText(title, allowEmpty: true)
            || !ValidWindowSelectorText(titleContains, allowEmpty: true))
        {
            return WindowWaitFailure(commandId, ErrorCodes.InvalidRequest, "Window selector values exceed their limits or contain control characters.");
        }
        if (occurrenceIndex is < 0 or > 1000)
            return WindowWaitFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (!WindowWaitConditions.TryNormalize(condition, out var normalizedCondition))
            return WindowWaitFailure(commandId, ErrorCodes.InvalidRequest, "condition must be one of: exists, not-exists, foreground, title-equals, title-contains.");
        if (WindowWaitConditions.RequiresExpectedTitle(normalizedCondition) && expectedTitle is null)
            return WindowWaitFailure(commandId, ErrorCodes.InvalidRequest, "expectedTitle is required for title-equals and title-contains conditions.");
        if (!ValidWindowSelectorText(expectedTitle, allowEmpty: true))
            return WindowWaitFailure(commandId, ErrorCodes.InvalidRequest, "expectedTitle exceeds its limit or contains control characters.");
        if (timeoutMs is < 1 or > MaxWindowWaitTimeoutMs)
            return WindowWaitFailure(commandId, ErrorCodes.InvalidRequest, $"timeoutMs must be between 1 and {MaxWindowWaitTimeoutMs}.");
        if (pollIntervalMs is < MinWindowWaitPollIntervalMs or > MaxWindowWaitPollIntervalMs)
            return WindowWaitFailure(commandId, ErrorCodes.InvalidRequest, $"pollIntervalMs must be between {MinWindowWaitPollIntervalMs} and {MaxWindowWaitPollIntervalMs}.");
        if (!OperatingSystem.IsWindows())
            return WindowWaitFailure(commandId, ErrorCodes.UiAutomationUnavailable, "Window waiting is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => WaitForWindowWindows(
                    parsedHandle,
                    processId,
                    processName?.Trim(),
                    className?.Trim(),
                    title,
                    titleContains,
                    occurrenceIndex,
                    normalizedCondition,
                    expectedTitle,
                    includeInvisible,
                    timeoutMs,
                    pollIntervalMs,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return WindowWaitFailure(commandId, ErrorCodes.CommandCancelled, "The window wait request was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected window wait failure for command {CommandId}", commandId);
            return WindowWaitFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while waiting for the window.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<WindowWaitResult> WaitForWindowWindows(
        IntPtr windowHandle,
        int? processId,
        string? processName,
        string? className,
        string? title,
        string? titleContains,
        int occurrenceIndex,
        string condition,
        string? expectedTitle,
        bool includeInvisible,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var pollCount = 0;
        WindowInfo? lastMatch = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = CaptureWindowSnapshot(
                includeInvisible,
                includeUntitled: true,
                cancellationToken);
            if (!snapshot.Completed)
            {
                return WindowWaitFailure(
                    commandId,
                    ErrorCodes.WindowEnumerationFailed,
                    "Windows could not enumerate top-level windows while waiting.");
            }

            pollCount++;
            lastMatch = snapshot.Windows
                .Where(window => MatchesWindowSelector(
                    window,
                    windowHandle,
                    processId,
                    processName,
                    className,
                    title,
                    titleContains))
                .Skip(occurrenceIndex)
                .FirstOrDefault();

            if (IsWindowWaitConditionSatisfied(condition, expectedTitle, lastMatch))
            {
                return new CommandResult<WindowWaitResult>
                {
                    CommandId = commandId,
                    Success = true,
                    Data = new WindowWaitResult
                    {
                        Condition = condition,
                        ExpectedTitle = WindowWaitConditions.RequiresExpectedTitle(condition)
                            ? expectedTitle
                            : null,
                        WaitedMs = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                        PollCount = pollCount,
                        OccurrenceIndex = occurrenceIndex,
                        WindowFound = lastMatch is not null,
                        Window = lastMatch
                    }
                };
            }

            var elapsedMs = stopwatch.ElapsedMilliseconds;
            if (elapsedMs >= timeoutMs)
                break;

            var remainingMs = timeoutMs - elapsedMs;
            var delayMs = (int)Math.Min(pollIntervalMs, remainingMs);
            Task.Delay(delayMs, cancellationToken).GetAwaiter().GetResult();
        }

        var lastState = lastMatch is null
            ? "No matching window was found on the final poll."
            : $"The final matching window was '{lastMatch.Title}' ({lastMatch.WindowHandle}), foreground={lastMatch.IsForeground}.";
        return WindowWaitFailure(
            commandId,
            ErrorCodes.WindowWaitTimeout,
            $"Condition '{condition}' was not satisfied within {timeoutMs} ms after {pollCount} polls. {lastState}");
    }

    private static bool MatchesWindowSelector(
        WindowInfo window,
        IntPtr windowHandle,
        int? processId,
        string? processName,
        string? className,
        string? title,
        string? titleContains)
    {
        if (windowHandle != IntPtr.Zero
            && !string.Equals(window.WindowHandle, FormatWindowHandle(windowHandle), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (processId.HasValue && window.ProcessId != processId.Value)
            return false;
        if (!string.IsNullOrWhiteSpace(processName)
            && !string.Equals(
                NormalizeProcessName(window.ProcessName),
                NormalizeProcessName(processName),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(className)
            && !string.Equals(window.ClassName, className, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (title is not null
            && !string.Equals(window.Title, title, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (titleContains is not null
            && !window.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsWindowWaitConditionSatisfied(
        string condition,
        string? expectedTitle,
        WindowInfo? window) =>
        condition switch
        {
            WindowWaitConditions.Exists => window is not null,
            WindowWaitConditions.NotExists => window is null,
            WindowWaitConditions.Foreground => window?.IsForeground == true,
            WindowWaitConditions.TitleEquals => window is not null
                && string.Equals(window.Title, expectedTitle, StringComparison.OrdinalIgnoreCase),
            WindowWaitConditions.TitleContains => window?.Title.Contains(
                expectedTitle!,
                StringComparison.OrdinalIgnoreCase) == true,
            _ => false
        };

    private static string NormalizeProcessName(string processName)
    {
        var normalized = processName.Trim();
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private static bool ValidWindowSelectorText(string? value, bool allowEmpty = false)
    {
        if (value is null)
            return true;
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            return false;
        return value.Length <= MaxWindowSelectorCharacters && !value.Any(char.IsControl);
    }

    private static CommandResult<WindowWaitResult> WindowWaitFailure(
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
