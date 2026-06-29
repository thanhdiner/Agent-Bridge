using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const int MaximumScreenInputCoordinate = 100000;
    private const int MaximumScreenInputTitleLength = 1024;
    private const int MaximumScreenDragDurationMs = 10000;
    private const int MaximumScreenDragSteps = 240;
    private const int MaximumScreenScrollNotches = 20;
    private const int ScreenWheelDelta = 120;
    private const uint ScreenMouseEventWheel = 0x0800;
    private const uint ScreenMouseEventHWheel = 0x1000;
    private const uint ScreenGaRootOwner = 3;

    public async Task<CommandResult<ScreenClickResult>> ClickScreenAsync(
        string expectedForegroundWindowHandle,
        int x,
        int y,
        int? monitorIndex,
        string button,
        int clickCount,
        int? expectedProcessId,
        string? expectedWindowTitle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(expectedForegroundWindowHandle, out var expectedHandle))
            return ScreenInputFailure<ScreenClickResult>(commandId, ErrorCodes.InvalidRequest, "expectedForegroundWindowHandle is invalid.");
        if (!IsValidScreenInputCoordinate(x) || !IsValidScreenInputCoordinate(y))
            return ScreenInputFailure<ScreenClickResult>(commandId, ErrorCodes.InvalidRequest, $"x and y must be between {-MaximumScreenInputCoordinate} and {MaximumScreenInputCoordinate}.");
        if (monitorIndex is < 0)
            return ScreenInputFailure<ScreenClickResult>(commandId, ErrorCodes.InvalidRequest, "monitorIndex must be zero or greater when provided.");

        var normalizedButton = button?.Trim().ToLowerInvariant();
        if (!WindowMouseButtons.IsSupported(normalizedButton))
            return ScreenInputFailure<ScreenClickResult>(commandId, ErrorCodes.InvalidRequest, "button must be left, right, or middle.");
        if (clickCount is < 1 or > 3)
            return ScreenInputFailure<ScreenClickResult>(commandId, ErrorCodes.InvalidRequest, "clickCount must be between 1 and 3.");
        if (!ValidateScreenInputGuardArguments(expectedProcessId, expectedWindowTitle, out var argumentError))
            return ScreenInputFailure<ScreenClickResult>(commandId, ErrorCodes.InvalidRequest, argumentError!);
        if (!OperatingSystem.IsWindows())
            return ScreenInputFailure<ScreenClickResult>(commandId, ErrorCodes.UiAutomationUnavailable, "Screen coordinate input is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => ClickScreenWindows(
                    expectedHandle,
                    x,
                    y,
                    monitorIndex,
                    normalizedButton!,
                    clickCount,
                    expectedProcessId,
                    expectedWindowTitle,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ScreenInputFailure<ScreenClickResult>(commandId, ErrorCodes.CommandCancelled, "The screen click request was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected screen click failure for command {CommandId}", commandId);
            return ScreenInputFailure<ScreenClickResult>(commandId, ErrorCodes.InternalError, "An unexpected error occurred while clicking the screen.");
        }
    }

    public async Task<CommandResult<ScreenDragResult>> DragScreenAsync(
        string expectedForegroundWindowHandle,
        int startX,
        int startY,
        int endX,
        int endY,
        int? startMonitorIndex,
        int? endMonitorIndex,
        string button,
        int durationMs,
        int steps,
        int? expectedProcessId,
        string? expectedWindowTitle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(expectedForegroundWindowHandle, out var expectedHandle))
            return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.InvalidRequest, "expectedForegroundWindowHandle is invalid.");
        if (!IsValidScreenInputCoordinate(startX) || !IsValidScreenInputCoordinate(startY)
            || !IsValidScreenInputCoordinate(endX) || !IsValidScreenInputCoordinate(endY))
        {
            return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.InvalidRequest,
                $"All coordinates must be between {-MaximumScreenInputCoordinate} and {MaximumScreenInputCoordinate}.");
        }
        if (startMonitorIndex is < 0 || endMonitorIndex is < 0)
            return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.InvalidRequest, "Monitor indexes must be zero or greater when provided.");

        var normalizedButton = button?.Trim().ToLowerInvariant();
        if (!WindowMouseButtons.IsSupported(normalizedButton))
            return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.InvalidRequest, "button must be left, right, or middle.");
        if (durationMs is < 0 or > MaximumScreenDragDurationMs)
            return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.InvalidRequest, $"durationMs must be between 0 and {MaximumScreenDragDurationMs}.");
        if (steps is < 1 or > MaximumScreenDragSteps)
            return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.InvalidRequest, $"steps must be between 1 and {MaximumScreenDragSteps}.");
        if (!ValidateScreenInputGuardArguments(expectedProcessId, expectedWindowTitle, out var argumentError))
            return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.InvalidRequest, argumentError!);
        if (!OperatingSystem.IsWindows())
            return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.UiAutomationUnavailable, "Screen coordinate input is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => DragScreenWindows(
                    expectedHandle,
                    startX,
                    startY,
                    endX,
                    endY,
                    startMonitorIndex,
                    endMonitorIndex,
                    normalizedButton!,
                    durationMs,
                    steps,
                    expectedProcessId,
                    expectedWindowTitle,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.CommandCancelled, "The screen drag request was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected screen drag failure for command {CommandId}", commandId);
            return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.InternalError, "An unexpected error occurred while dragging on the screen.");
        }
    }

    public async Task<CommandResult<ScreenScrollResult>> ScrollScreenAsync(
        string expectedForegroundWindowHandle,
        int x,
        int y,
        int? monitorIndex,
        string direction,
        int notches,
        int? expectedProcessId,
        string? expectedWindowTitle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(expectedForegroundWindowHandle, out var expectedHandle))
            return ScreenInputFailure<ScreenScrollResult>(commandId, ErrorCodes.InvalidRequest, "expectedForegroundWindowHandle is invalid.");
        if (!IsValidScreenInputCoordinate(x) || !IsValidScreenInputCoordinate(y))
            return ScreenInputFailure<ScreenScrollResult>(commandId, ErrorCodes.InvalidRequest, $"x and y must be between {-MaximumScreenInputCoordinate} and {MaximumScreenInputCoordinate}.");
        if (monitorIndex is < 0)
            return ScreenInputFailure<ScreenScrollResult>(commandId, ErrorCodes.InvalidRequest, "monitorIndex must be zero or greater when provided.");
        if (!ScreenScrollDirections.TryNormalize(direction, out var normalizedDirection))
            return ScreenInputFailure<ScreenScrollResult>(commandId, ErrorCodes.InvalidRequest, "direction must be up, down, left, or right.");
        if (notches is < 1 or > MaximumScreenScrollNotches)
            return ScreenInputFailure<ScreenScrollResult>(commandId, ErrorCodes.InvalidRequest, $"notches must be between 1 and {MaximumScreenScrollNotches}.");
        if (!ValidateScreenInputGuardArguments(expectedProcessId, expectedWindowTitle, out var argumentError))
            return ScreenInputFailure<ScreenScrollResult>(commandId, ErrorCodes.InvalidRequest, argumentError!);
        if (!OperatingSystem.IsWindows())
            return ScreenInputFailure<ScreenScrollResult>(commandId, ErrorCodes.UiAutomationUnavailable, "Screen coordinate input is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => ScrollScreenWindows(
                    expectedHandle,
                    x,
                    y,
                    monitorIndex,
                    normalizedDirection,
                    notches,
                    expectedProcessId,
                    expectedWindowTitle,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ScreenInputFailure<ScreenScrollResult>(commandId, ErrorCodes.CommandCancelled, "The screen scroll request was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected screen scroll failure for command {CommandId}", commandId);
            return ScreenInputFailure<ScreenScrollResult>(commandId, ErrorCodes.InternalError, "An unexpected error occurred while scrolling the screen.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<ScreenClickResult> ClickScreenWindows(
        IntPtr expectedHandle,
        int x,
        int y,
        int? monitorIndex,
        string button,
        int clickCount,
        int? expectedProcessId,
        string? expectedWindowTitle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var monitors = EnumerateScreenMonitors();
        if (monitors.Count == 0)
            return ScreenInputFailure<ScreenClickResult>(commandId, ErrorCodes.ScreenInputFailed, "Windows did not report any active monitors.");

        var virtualBounds = ReadVirtualScreenBounds(monitors);
        if (!TryResolveScreenInputPoint(monitors, virtualBounds, x, y, monitorIndex, out var actualMonitorIndex, out var pointError))
            return ScreenInputFailure<ScreenClickResult>(commandId, pointError!.Code, pointError.Message);

        var guardError = ValidateScreenInputGuard(expectedHandle, expectedProcessId, expectedWindowTitle, out var guard);
        if (guardError is not null)
            return ScreenInputFailure<ScreenClickResult>(commandId, guardError.Code, guardError.Message);
        var hitError = ValidateScreenInputHit(expectedHandle, guard, x, y, out var hitWindow, out var hitRootOwner);
        if (hitError is not null)
            return ScreenInputFailure<ScreenClickResult>(commandId, hitError.Code, hitError.Message);

        cancellationToken.ThrowIfCancellationRequested();
        guardError = ValidateScreenInputGuard(expectedHandle, expectedProcessId, expectedWindowTitle, out guard);
        if (guardError is not null)
            return ScreenInputFailure<ScreenClickResult>(commandId, guardError.Code, guardError.Message);

        var inputs = BuildWindowClickInputs(
            x,
            y,
            virtualBounds.X,
            virtualBounds.Y,
            virtualBounds.Width,
            virtualBounds.Height,
            button,
            clickCount);
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (sent != (uint)inputs.Length)
        {
            ReleaseWindowClickButton(button);
            return ScreenInputFailure<ScreenClickResult>(commandId, ErrorCodes.ScreenInputFailed, $"SendInput accepted {sent} of {inputs.Length} mouse events.");
        }

        Thread.Sleep(25);
        return new CommandResult<ScreenClickResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new ScreenClickResult
            {
                Guard = guard.ToResult(),
                VirtualScreenBounds = virtualBounds.ToUiBounds(),
                MonitorIndex = actualMonitorIndex,
                X = x,
                Y = y,
                HitWindowHandle = FormatWindowHandle(hitWindow),
                HitRootOwnerWindowHandle = FormatWindowHandle(hitRootOwner),
                Button = button,
                ClickCount = clickCount,
                Clicked = true
            }
        };
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<ScreenDragResult> DragScreenWindows(
        IntPtr expectedHandle,
        int startX,
        int startY,
        int endX,
        int endY,
        int? startMonitorIndex,
        int? endMonitorIndex,
        string button,
        int durationMs,
        int steps,
        int? expectedProcessId,
        string? expectedWindowTitle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var monitors = EnumerateScreenMonitors();
        if (monitors.Count == 0)
            return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.ScreenInputFailed, "Windows did not report any active monitors.");

        var virtualBounds = ReadVirtualScreenBounds(monitors);
        if (!TryResolveScreenInputPoint(monitors, virtualBounds, startX, startY, startMonitorIndex, out var actualStartMonitor, out var startError))
            return ScreenInputFailure<ScreenDragResult>(commandId, startError!.Code, startError.Message);
        if (!TryResolveScreenInputPoint(monitors, virtualBounds, endX, endY, endMonitorIndex, out var actualEndMonitor, out var endError))
            return ScreenInputFailure<ScreenDragResult>(commandId, endError!.Code, endError.Message);

        var guardError = ValidateScreenInputGuard(expectedHandle, expectedProcessId, expectedWindowTitle, out var guard);
        if (guardError is not null)
            return ScreenInputFailure<ScreenDragResult>(commandId, guardError.Code, guardError.Message);
        var hitError = ValidateScreenInputHit(expectedHandle, guard, startX, startY, out var hitWindow, out var hitRootOwner);
        if (hitError is not null)
            return ScreenInputFailure<ScreenDragResult>(commandId, hitError.Code, hitError.Message);

        cancellationToken.ThrowIfCancellationRequested();
        guardError = ValidateScreenInputGuard(expectedHandle, expectedProcessId, expectedWindowTitle, out guard);
        if (guardError is not null)
            return ScreenInputFailure<ScreenDragResult>(commandId, guardError.Code, guardError.Message);

        var (downFlag, upFlag) = GetWindowClickButtonFlags(button);
        var buttonHeld = false;
        try
        {
            if (!SendWindowDragInput(BuildWindowMouseInput(
                    NormalizeWindowClickCoordinate(startX, virtualBounds.X, virtualBounds.Width),
                    NormalizeWindowClickCoordinate(startY, virtualBounds.Y, virtualBounds.Height),
                    MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk)))
            {
                return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.ScreenInputFailed, "Windows rejected the pointer move to the drag start point.");
            }

            if (!SendWindowDragInput(BuildWindowMouseInput(0, 0, downFlag)))
                return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.ScreenInputFailed, "Windows rejected the mouse button down event.");

            buttonHeld = true;
            var stopwatch = Stopwatch.StartNew();
            for (var step = 1; step <= steps; step++)
            {
                WaitForWindowDragStep(stopwatch, durationMs, step, steps, cancellationToken);
                if (!ScreenForegroundMatches(expectedHandle))
                {
                    return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.ScreenForegroundMismatch,
                        "The foreground window changed while the drag was in progress.");
                }

                var currentX = InterpolateWindowDragCoordinate(startX, endX, step, steps);
                var currentY = InterpolateWindowDragCoordinate(startY, endY, step, steps);
                if (!SendWindowDragInput(BuildWindowMouseInput(
                        NormalizeWindowClickCoordinate(currentX, virtualBounds.X, virtualBounds.Width),
                        NormalizeWindowClickCoordinate(currentY, virtualBounds.Y, virtualBounds.Height),
                        MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk)))
                {
                    return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.ScreenInputFailed,
                        $"Windows rejected pointer movement at drag step {step} of {steps}.");
                }
            }

            if (!SendWindowDragInput(BuildWindowMouseInput(0, 0, upFlag)))
                return ScreenInputFailure<ScreenDragResult>(commandId, ErrorCodes.ScreenInputFailed, "Windows rejected the mouse button up event.");

            buttonHeld = false;
        }
        finally
        {
            if (buttonHeld)
                ReleaseWindowClickButton(button);
        }

        Thread.Sleep(25);
        return new CommandResult<ScreenDragResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new ScreenDragResult
            {
                Guard = guard.ToResult(),
                VirtualScreenBounds = virtualBounds.ToUiBounds(),
                StartMonitorIndex = actualStartMonitor,
                EndMonitorIndex = actualEndMonitor,
                StartX = startX,
                StartY = startY,
                EndX = endX,
                EndY = endY,
                HitWindowHandle = FormatWindowHandle(hitWindow),
                HitRootOwnerWindowHandle = FormatWindowHandle(hitRootOwner),
                Button = button,
                DurationMs = durationMs,
                Steps = steps,
                Dragged = true
            }
        };
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<ScreenScrollResult> ScrollScreenWindows(
        IntPtr expectedHandle,
        int x,
        int y,
        int? monitorIndex,
        string direction,
        int notches,
        int? expectedProcessId,
        string? expectedWindowTitle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var monitors = EnumerateScreenMonitors();
        if (monitors.Count == 0)
            return ScreenInputFailure<ScreenScrollResult>(commandId, ErrorCodes.ScreenInputFailed, "Windows did not report any active monitors.");

        var virtualBounds = ReadVirtualScreenBounds(monitors);
        if (!TryResolveScreenInputPoint(monitors, virtualBounds, x, y, monitorIndex, out var actualMonitorIndex, out var pointError))
            return ScreenInputFailure<ScreenScrollResult>(commandId, pointError!.Code, pointError.Message);

        var guardError = ValidateScreenInputGuard(expectedHandle, expectedProcessId, expectedWindowTitle, out var guard);
        if (guardError is not null)
            return ScreenInputFailure<ScreenScrollResult>(commandId, guardError.Code, guardError.Message);
        var hitError = ValidateScreenInputHit(expectedHandle, guard, x, y, out var hitWindow, out var hitRootOwner);
        if (hitError is not null)
            return ScreenInputFailure<ScreenScrollResult>(commandId, hitError.Code, hitError.Message);

        cancellationToken.ThrowIfCancellationRequested();
        guardError = ValidateScreenInputGuard(expectedHandle, expectedProcessId, expectedWindowTitle, out guard);
        if (guardError is not null)
            return ScreenInputFailure<ScreenScrollResult>(commandId, guardError.Code, guardError.Message);

        var wheelDelta = GetScreenWheelDelta(direction, notches);
        var wheelFlag = direction is ScreenScrollDirections.Up or ScreenScrollDirections.Down
            ? ScreenMouseEventWheel
            : ScreenMouseEventHWheel;
        var inputs = new[]
        {
            BuildWindowMouseInput(
                NormalizeWindowClickCoordinate(x, virtualBounds.X, virtualBounds.Width),
                NormalizeWindowClickCoordinate(y, virtualBounds.Y, virtualBounds.Height),
                MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk),
            BuildScreenMouseInput(0, 0, unchecked((uint)wheelDelta), wheelFlag)
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (sent != (uint)inputs.Length)
            return ScreenInputFailure<ScreenScrollResult>(commandId, ErrorCodes.ScreenInputFailed, $"SendInput accepted {sent} of {inputs.Length} mouse events.");

        Thread.Sleep(25);
        return new CommandResult<ScreenScrollResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new ScreenScrollResult
            {
                Guard = guard.ToResult(),
                VirtualScreenBounds = virtualBounds.ToUiBounds(),
                MonitorIndex = actualMonitorIndex,
                X = x,
                Y = y,
                HitWindowHandle = FormatWindowHandle(hitWindow),
                HitRootOwnerWindowHandle = FormatWindowHandle(hitRootOwner),
                Direction = direction,
                Notches = notches,
                WheelDelta = wheelDelta,
                Scrolled = true
            }
        };
    }

    internal static bool TryResolveScreenInputPoint(
        IReadOnlyList<ScreenMonitorSnapshot> monitors,
        ScreenCaptureBounds virtualBounds,
        int x,
        int y,
        int? expectedMonitorIndex,
        out int actualMonitorIndex,
        out CommandError? error)
    {
        actualMonitorIndex = -1;
        error = null;

        if (!ContainsScreenPoint(virtualBounds, x, y))
        {
            error = new CommandError(ErrorCodes.ScreenPointOutOfBounds,
                $"The point ({x}, {y}) is outside the virtual desktop bounds.");
            return false;
        }

        var monitor = monitors.FirstOrDefault(candidate => ContainsScreenPoint(candidate.Bounds, x, y));
        if (monitor is null)
        {
            error = new CommandError(ErrorCodes.ScreenPointOutOfBounds,
                $"The point ({x}, {y}) lies in a gap between active monitors.");
            return false;
        }

        actualMonitorIndex = monitor.Index;
        if (!expectedMonitorIndex.HasValue)
            return true;
        if (expectedMonitorIndex.Value >= monitors.Count)
        {
            error = new CommandError(ErrorCodes.InvalidRequest,
                $"monitorIndex must be between 0 and {monitors.Count - 1}.");
            return false;
        }
        if (actualMonitorIndex != expectedMonitorIndex.Value)
        {
            error = new CommandError(ErrorCodes.ScreenMonitorMismatch,
                $"The point is on monitor {actualMonitorIndex}, not expected monitor {expectedMonitorIndex.Value}.");
            return false;
        }

        return true;
    }

    internal static int GetScreenWheelDelta(string direction, int notches) => direction switch
    {
        ScreenScrollDirections.Up or ScreenScrollDirections.Right => checked(ScreenWheelDelta * notches),
        ScreenScrollDirections.Down or ScreenScrollDirections.Left => checked(-ScreenWheelDelta * notches),
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    private static bool ValidateScreenInputGuardArguments(
        int? expectedProcessId,
        string? expectedWindowTitle,
        out string? error)
    {
        error = null;
        if (expectedProcessId is <= 0)
        {
            error = "expectedProcessId must be greater than zero when provided.";
            return false;
        }
        if (expectedWindowTitle is not null
            && (expectedWindowTitle.Length > MaximumScreenInputTitleLength || expectedWindowTitle.Any(char.IsControl)))
        {
            error = $"expectedWindowTitle must be at most {MaximumScreenInputTitleLength} characters without control characters.";
            return false;
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static CommandError? ValidateScreenInputGuard(
        IntPtr expectedHandle,
        int? expectedProcessId,
        string? expectedWindowTitle,
        out ScreenInputGuardSnapshot snapshot)
    {
        snapshot = default!;
        if (!IsWindow(expectedHandle))
            return new CommandError(ErrorCodes.WindowNotFound, "The expected foreground window no longer exists.");

        var actualForeground = GetForegroundWindow();
        if (actualForeground == IntPtr.Zero)
            return new CommandError(ErrorCodes.ScreenForegroundMismatch, "Windows did not report a foreground window.");

        var expectedRootOwner = GetScreenRootOwner(expectedHandle);
        var actualRootOwner = GetScreenRootOwner(actualForeground);
        if (actualForeground != expectedHandle
            && actualRootOwner != expectedHandle
            && actualRootOwner != expectedRootOwner)
        {
            return new CommandError(ErrorCodes.ScreenForegroundMismatch,
                $"The foreground window changed: expected {FormatWindowHandle(expectedHandle)}, actual {FormatWindowHandle(actualForeground)}.");
        }

        GetWindowThreadProcessId(expectedHandle, out var rawProcessId);
        var processId = rawProcessId <= int.MaxValue ? (int)rawProcessId : 0;
        var title = ReadWindowTitle(expectedHandle);
        if (expectedProcessId.HasValue && processId != expectedProcessId.Value)
        {
            return new CommandError(ErrorCodes.WindowGuardMismatch,
                $"The foreground guard process id changed: expected {expectedProcessId.Value}, actual {processId}.");
        }
        if (expectedWindowTitle is not null
            && !string.Equals(title, expectedWindowTitle, StringComparison.OrdinalIgnoreCase))
        {
            return new CommandError(ErrorCodes.WindowGuardMismatch,
                "The foreground guard title no longer matches expectedWindowTitle.");
        }

        snapshot = new ScreenInputGuardSnapshot(
            expectedHandle,
            actualForeground,
            expectedRootOwner,
            actualRootOwner,
            title,
            processId,
            ReadProcessName(rawProcessId));
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static CommandError? ValidateScreenInputHit(
        IntPtr expectedHandle,
        ScreenInputGuardSnapshot guard,
        int x,
        int y,
        out IntPtr hitWindow,
        out IntPtr hitRootOwner)
    {
        hitWindow = WindowFromPoint(new WindowClickPoint { X = x, Y = y });
        hitRootOwner = hitWindow == IntPtr.Zero ? IntPtr.Zero : GetScreenRootOwner(hitWindow);
        if (hitWindow == IntPtr.Zero)
            return new CommandError(ErrorCodes.ScreenHitTargetMismatch, "No window exists at the requested screen point.");

        if (hitWindow != expectedHandle
            && hitRootOwner != expectedHandle
            && hitRootOwner != guard.ExpectedRootOwner)
        {
            return new CommandError(ErrorCodes.ScreenHitTargetMismatch,
                "The requested point is not owned by the guarded foreground window; no input was sent.");
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static bool ScreenForegroundMatches(IntPtr expectedHandle)
    {
        var actual = GetForegroundWindow();
        if (actual == IntPtr.Zero)
            return false;
        var expectedRoot = GetScreenRootOwner(expectedHandle);
        var actualRoot = GetScreenRootOwner(actual);
        return actual == expectedHandle || actualRoot == expectedHandle || actualRoot == expectedRoot;
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr GetScreenRootOwner(IntPtr handle)
    {
        var root = GetAncestor(handle, ScreenGaRootOwner);
        return root == IntPtr.Zero ? handle : root;
    }

    private static bool ContainsScreenPoint(ScreenCaptureBounds bounds, int x, int y) =>
        x >= bounds.X && x < bounds.Right && y >= bounds.Y && y < bounds.Bottom;

    private static bool IsValidScreenInputCoordinate(int value) =>
        value is >= -MaximumScreenInputCoordinate and <= MaximumScreenInputCoordinate;

    private static NativeInput BuildScreenMouseInput(int x, int y, uint mouseData, uint flags) => new()
    {
        Type = InputMouse,
        Data = new NativeInputUnion
        {
            Mouse = new NativeMouseInput
            {
                X = x,
                Y = y,
                MouseData = mouseData,
                Flags = flags
            }
        }
    };

    private static CommandResult<T> ScreenInputFailure<T>(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };

    private sealed record ScreenInputGuardSnapshot(
        IntPtr ExpectedForeground,
        IntPtr ActualForeground,
        IntPtr ExpectedRootOwner,
        IntPtr ActualRootOwner,
        string Title,
        int ProcessId,
        string ProcessName)
    {
        public ScreenInputGuardInfo ToResult() => new()
        {
            ExpectedForegroundWindowHandle = FormatWindowHandle(ExpectedForeground),
            ActualForegroundWindowHandle = FormatWindowHandle(ActualForeground),
            ExpectedRootOwnerWindowHandle = FormatWindowHandle(ExpectedRootOwner),
            ActualRootOwnerWindowHandle = FormatWindowHandle(ActualRootOwner),
            Title = Title,
            ProcessId = ProcessId,
            ProcessName = ProcessName
        };
    }
}
