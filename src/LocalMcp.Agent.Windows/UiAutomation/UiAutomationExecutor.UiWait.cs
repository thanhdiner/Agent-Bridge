using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const int MaxUiWaitTimeoutMs = 300_000;
    private const int MinUiWaitPollIntervalMs = 25;
    private const int MaxUiWaitPollIntervalMs = 5_000;

    public async Task<CommandResult<UiWaitResult>> WaitAsync(
        string windowHandle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        string condition,
        string? expectedValue,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return WaitFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            return WaitFailure(commandId, ErrorCodes.InvalidRequest, "automationId or name is required.");
        if (occurrenceIndex is < 0 or > 1000)
            return WaitFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (!UiWaitConditions.TryNormalize(condition, out var normalizedCondition))
            return WaitFailure(commandId, ErrorCodes.InvalidRequest, "condition must be one of: exists, not-exists, enabled, disabled, value-equals, value-contains.");
        if (UiWaitConditions.RequiresExpectedValue(normalizedCondition) && expectedValue is null)
            return WaitFailure(commandId, ErrorCodes.InvalidRequest, "expectedValue is required for value-equals and value-contains conditions.");
        if (expectedValue?.Length > MaxSetValueCharacters)
            return WaitFailure(commandId, ErrorCodes.InvalidRequest, $"expectedValue must be at most {MaxSetValueCharacters} characters.");
        if (timeoutMs is < 1 or > MaxUiWaitTimeoutMs)
            return WaitFailure(commandId, ErrorCodes.InvalidRequest, $"timeoutMs must be between 1 and {MaxUiWaitTimeoutMs}.");
        if (pollIntervalMs is < MinUiWaitPollIntervalMs or > MaxUiWaitPollIntervalMs)
            return WaitFailure(commandId, ErrorCodes.InvalidRequest, $"pollIntervalMs must be between {MinUiWaitPollIntervalMs} and {MaxUiWaitPollIntervalMs}.");
        if (!OperatingSystem.IsWindows())
            return WaitFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI waiting is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => WaitWindows(
                    handle,
                    automationId?.Trim(),
                    name?.Trim(),
                    controlType?.Trim(),
                    occurrenceIndex,
                    normalizedCondition,
                    expectedValue,
                    timeoutMs,
                    pollIntervalMs,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return WaitFailure(commandId, ErrorCodes.CommandCancelled, "The UI wait request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation wait failure for command {CommandId}", commandId);
            return WaitFailure(commandId, ErrorCodes.UiAutomationFailed, "Windows UI Automation could not observe the requested control.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI wait failure for command {CommandId}", commandId);
            return WaitFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while waiting for the control.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiWaitResult> WaitWindows(
        IntPtr handle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        string condition,
        string? expectedValue,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return WaitFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        var stopwatch = Stopwatch.StartNew();
        var pollCount = 0;
        var lastObservation = UiWaitObservation.NotFound;

        while (true)
        {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsWindow(handle))
                    return WaitFailure(commandId, ErrorCodes.WindowNotFound, "The target window closed while waiting for the UI condition.");

                IUIAutomation? automation = null;
                IUIAutomationTreeWalker? walker = null;
                IUIAutomationElement? root = null;
                IUIAutomationElement? match = null;
                try
                {
                    automation = CreateAutomationClient();
                    walker = automation.ControlViewWalker;
                    root = automation.ElementFromHandle(handle);
                    if (root is null)
                        return WaitFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");

                    var seen = 0;
                    var visited = 0;
                    match = FindTarget(
                        root,
                        walker,
                        automationId,
                        name,
                        controlType,
                        occurrenceIndex,
                        ref seen,
                        ref visited,
                        cancellationToken);

                    pollCount++;
                    lastObservation = ObserveWaitTarget(match);

                    if (UiWaitConditions.RequiresExpectedValue(condition)
                        && lastObservation.ElementFound)
                    {
                        if (lastObservation.IsPassword)
                            return WaitFailure(commandId, ErrorCodes.UiValueNotSupported, "Value conditions are not supported for password controls.");
                        if (!lastObservation.ValueSupported)
                            return WaitFailure(commandId, ErrorCodes.UiValueNotSupported, "The matched control does not expose a readable value pattern.");
                    }

                    if (IsWaitConditionSatisfied(condition, expectedValue, lastObservation))
                    {
                        return new CommandResult<UiWaitResult>
                        {
                            CommandId = commandId,
                            Success = true,
                            Data = CreateWaitResult(
                                handle,
                                condition,
                                expectedValue,
                                occurrenceIndex,
                                stopwatch.ElapsedMilliseconds,
                                pollCount,
                                lastObservation)
                        };
                    }
                }
                finally
                {
                    if (!ReferenceEquals(match, root))
                        ReleaseComObject(match);
                    ReleaseComObject(root);
                    ReleaseComObject(walker);
                    ReleaseComObject(automation);
                }

                var elapsedMs = stopwatch.ElapsedMilliseconds;
                if (elapsedMs >= timeoutMs)
                    break;

                var remainingMs = timeoutMs - elapsedMs;
                var delayMs = (int)Math.Min(pollIntervalMs, remainingMs);
                Task.Delay(delayMs, cancellationToken).GetAwaiter().GetResult();
            }

        var state = lastObservation.ElementFound
            ? $"The last matched control was {(lastObservation.Enabled == true ? "enabled" : "disabled")}."
            : "No matching control was found on the final poll.";
        return WaitFailure(
            commandId,
            ErrorCodes.UiWaitTimeout,
            $"Condition '{condition}' was not satisfied within {timeoutMs} ms after {pollCount} polls. {state}");
    }

    [SupportedOSPlatform("windows")]
    private static UiWaitObservation ObserveWaitTarget(IUIAutomationElement? element)
    {
        if (element is null)
            return UiWaitObservation.NotFound;

        var isPassword = ReadOrDefault(() => element.CurrentIsPassword != 0, false);
        var value = ReadValue(element, isPassword, out var valueTruncated);
        return new UiWaitObservation
        {
            ElementFound = true,
            Name = LimitMetadata(ReadOrDefault(() => element.CurrentName, string.Empty)),
            AutomationId = LimitMetadata(ReadOrDefault(() => element.CurrentAutomationId, string.Empty)),
            ControlType = GetControlTypeName(ReadOrDefault(() => element.CurrentControlType, 0)),
            Bounds = ReadBounds(element),
            Enabled = ReadOrDefault(() => element.CurrentIsEnabled != 0, false),
            IsPassword = isPassword,
            ValueSupported = !isPassword && value is not null,
            Value = isPassword ? null : value,
            ValueTruncated = !isPassword && valueTruncated
        };
    }

    private static bool IsWaitConditionSatisfied(
        string condition,
        string? expectedValue,
        UiWaitObservation observation) =>
        condition switch
        {
            UiWaitConditions.Exists => observation.ElementFound,
            UiWaitConditions.NotExists => !observation.ElementFound,
            UiWaitConditions.Enabled => observation.ElementFound && observation.Enabled == true,
            UiWaitConditions.Disabled => observation.ElementFound && observation.Enabled == false,
            UiWaitConditions.ValueEquals => observation.ElementFound
                && observation.ValueSupported
                && string.Equals(observation.Value, expectedValue, StringComparison.Ordinal),
            UiWaitConditions.ValueContains => observation.ElementFound
                && observation.ValueSupported
                && observation.Value?.Contains(expectedValue!, StringComparison.Ordinal) == true,
            _ => false
        };

    private static UiWaitResult CreateWaitResult(
        IntPtr handle,
        string condition,
        string? expectedValue,
        int occurrenceIndex,
        long waitedMs,
        int pollCount,
        UiWaitObservation observation) =>
        new()
        {
            WindowHandle = FormatWindowHandle(handle),
            Condition = condition,
            ExpectedValue = UiWaitConditions.RequiresExpectedValue(condition) ? expectedValue : null,
            WaitedMs = (int)Math.Min(int.MaxValue, waitedMs),
            PollCount = pollCount,
            OccurrenceIndex = occurrenceIndex,
            ElementFound = observation.ElementFound,
            Name = observation.Name,
            AutomationId = observation.AutomationId,
            ControlType = observation.ControlType,
            Bounds = observation.Bounds,
            Enabled = observation.Enabled,
            IsPassword = observation.IsPassword,
            ValueSupported = observation.ValueSupported,
            Value = observation.Value,
            ValueTruncated = observation.ValueTruncated
        };

    private static CommandResult<UiWaitResult> WaitFailure(Guid commandId, string code, string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };

    private sealed record UiWaitObservation
    {
        public static readonly UiWaitObservation NotFound = new();

        public bool ElementFound { get; init; }
        public string? Name { get; init; }
        public string? AutomationId { get; init; }
        public string? ControlType { get; init; }
        public UiBounds? Bounds { get; init; }
        public bool? Enabled { get; init; }
        public bool IsPassword { get; init; }
        public bool ValueSupported { get; init; }
        public string? Value { get; init; }
        public bool ValueTruncated { get; init; }
    }
}
