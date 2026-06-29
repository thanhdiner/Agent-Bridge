using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const int MaximumWindowDragCoordinate = 100000;
    private const int MaximumWindowDragDurationMs = 10000;
    private const int MaximumWindowDragSteps = 240;
    private const int MaximumWindowDragTitleLength = 1024;

    public async Task<CommandResult<WindowDragResult>> DragWindowAsync(
        string windowHandle,
        int startX,
        int startY,
        int endX,
        int endY,
        string button,
        int durationMs,
        int steps,
        int? expectedProcessId,
        string? expectedWindowTitle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return WindowDragFailure(commandId, ErrorCodes.InvalidRequest, "Invalid windowHandle.");
        if (!IsValidWindowDragStartCoordinate(startX) || !IsValidWindowDragStartCoordinate(startY))
        {
            return WindowDragFailure(commandId, ErrorCodes.InvalidRequest,
                $"startX and startY must be between 0 and {MaximumWindowDragCoordinate}.");
        }
        if (!IsValidWindowDragEndCoordinate(endX) || !IsValidWindowDragEndCoordinate(endY))
        {
            return WindowDragFailure(commandId, ErrorCodes.InvalidRequest,
                $"endX and endY must be between {-MaximumWindowDragCoordinate} and {MaximumWindowDragCoordinate}.");
        }

        var normalizedButton = button?.Trim().ToLowerInvariant();
        if (!WindowMouseButtons.IsSupported(normalizedButton))
            return WindowDragFailure(commandId, ErrorCodes.InvalidRequest, "button must be left, right, or middle.");
        if (durationMs is < 0 or > MaximumWindowDragDurationMs)
            return WindowDragFailure(commandId, ErrorCodes.InvalidRequest,
                $"durationMs must be between 0 and {MaximumWindowDragDurationMs}.");
        if (steps is < 1 or > MaximumWindowDragSteps)
            return WindowDragFailure(commandId, ErrorCodes.InvalidRequest,
                $"steps must be between 1 and {MaximumWindowDragSteps}.");
        if (expectedProcessId is <= 0)
            return WindowDragFailure(commandId, ErrorCodes.InvalidRequest,
                "expectedProcessId must be greater than zero when provided.");
        if (expectedWindowTitle is not null &&
            (expectedWindowTitle.Length > MaximumWindowDragTitleLength || expectedWindowTitle.Any(char.IsControl)))
        {
            return WindowDragFailure(commandId, ErrorCodes.InvalidRequest,
                $"expectedWindowTitle must be at most {MaximumWindowDragTitleLength} characters without control characters.");
        }
        if (!OperatingSystem.IsWindows())
            return WindowDragFailure(commandId, ErrorCodes.UiAutomationUnavailable,
                "Window coordinate dragging is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => DragWindowWindows(handle, startX, startY, endX, endY, normalizedButton!, durationMs, steps,
                    expectedProcessId, expectedWindowTitle, commandId, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return WindowDragFailure(commandId, ErrorCodes.CommandCancelled, "The window drag request was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected window drag failure for command {CommandId}", commandId);
            return WindowDragFailure(commandId, ErrorCodes.InternalError,
                "An unexpected error occurred while dragging in the window.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<WindowDragResult> DragWindowWindows(
        IntPtr handle,
        int startX,
        int startY,
        int endX,
        int endY,
        string button,
        int durationMs,
        int steps,
        int? expectedProcessId,
        string? expectedWindowTitle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return WindowDragFailure(commandId, ErrorCodes.WindowNotFound, "Window not found.");

        var guardError = ValidateWindowClickGuards(handle, expectedProcessId, expectedWindowTitle, out _, out _);
        if (guardError is not null)
            return WindowDragFailure(commandId, guardError.Code, guardError.Message);

        cancellationToken.ThrowIfCancellationRequested();
        var focusResult = FocusWindowWindows(handle, commandId, cancellationToken);
        if (!focusResult.Success || focusResult.Data is null)
        {
            return WindowDragFailure(commandId,
                focusResult.Error?.Code ?? ErrorCodes.WindowDragFailed,
                focusResult.Error?.Message ?? "The target window could not be focused before dragging.");
        }

        guardError = ValidateWindowClickGuards(handle, expectedProcessId, expectedWindowTitle,
            out var processId, out var title);
        if (guardError is not null)
            return WindowDragFailure(commandId, guardError.Code, guardError.Message);

        if (!GetWindowRect(handle, out var initialRectangle))
            return WindowDragFailure(commandId, ErrorCodes.WindowDragFailed,
                "The target window bounds could not be read.");

        var initialWidth = initialRectangle.Right - initialRectangle.Left;
        var initialHeight = initialRectangle.Bottom - initialRectangle.Top;
        if (!TryTranslateWindowClickPoint(initialRectangle.Left, initialRectangle.Top, initialWidth, initialHeight,
                startX, startY, out var startScreenX, out var startScreenY))
        {
            return WindowDragFailure(commandId, ErrorCodes.WindowPointOutOfBounds,
                $"The local start point ({startX}, {startY}) is outside the current window bounds {initialWidth}x{initialHeight}.");
        }

        if (!TryTranslateWindowDragEndpoint(initialRectangle.Left, initialRectangle.Top, endX, endY,
                out var endScreenX, out var endScreenY))
        {
            return WindowDragFailure(commandId, ErrorCodes.WindowPointOutOfBounds,
                "The local end point overflowed screen coordinates.");
        }

        var virtualLeft = GetSystemMetrics(SmXVirtualScreen);
        var virtualTop = GetSystemMetrics(SmYVirtualScreen);
        var virtualWidth = GetSystemMetrics(SmCxVirtualScreen);
        var virtualHeight = GetSystemMetrics(SmCyVirtualScreen);
        if (!IsWindowDragPointOnVirtualDesktop(startScreenX, startScreenY, virtualLeft, virtualTop, virtualWidth, virtualHeight) ||
            !IsWindowDragPointOnVirtualDesktop(endScreenX, endScreenY, virtualLeft, virtualTop, virtualWidth, virtualHeight))
        {
            return WindowDragFailure(commandId, ErrorCodes.WindowPointOutOfBounds,
                "The translated drag path is outside the Windows virtual desktop.");
        }

        var hitWindow = WindowFromPoint(new WindowClickPoint { X = startScreenX, Y = startScreenY });
        var hitRoot = hitWindow == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hitWindow, GaRoot);
        if (hitRoot != handle)
        {
            return WindowDragFailure(commandId, ErrorCodes.WindowDragFailed,
                "The requested start point is currently covered by another window; no input was sent.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (downFlag, upFlag) = GetWindowClickButtonFlags(button);
        var buttonHeld = false;
        try
        {
            if (!SendWindowDragInput(BuildWindowMouseInput(
                    NormalizeWindowClickCoordinate(startScreenX, virtualLeft, virtualWidth),
                    NormalizeWindowClickCoordinate(startScreenY, virtualTop, virtualHeight),
                    MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk)))
            {
                return WindowDragFailure(commandId, ErrorCodes.WindowDragFailed,
                    "Windows rejected the pointer move to the drag start point.");
            }

            if (!SendWindowDragInput(BuildWindowMouseInput(0, 0, downFlag)))
                return WindowDragFailure(commandId, ErrorCodes.WindowDragFailed,
                    "Windows rejected the mouse button down event.");

            buttonHeld = true;
            var stopwatch = Stopwatch.StartNew();
            for (var step = 1; step <= steps; step++)
            {
                WaitForWindowDragStep(stopwatch, durationMs, step, steps, cancellationToken);
                var screenX = InterpolateWindowDragCoordinate(startScreenX, endScreenX, step, steps);
                var screenY = InterpolateWindowDragCoordinate(startScreenY, endScreenY, step, steps);
                if (!SendWindowDragInput(BuildWindowMouseInput(
                        NormalizeWindowClickCoordinate(screenX, virtualLeft, virtualWidth),
                        NormalizeWindowClickCoordinate(screenY, virtualTop, virtualHeight),
                        MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk)))
                {
                    return WindowDragFailure(commandId, ErrorCodes.WindowDragFailed,
                        $"Windows rejected pointer movement at drag step {step} of {steps}.");
                }
            }

            if (!SendWindowDragInput(BuildWindowMouseInput(0, 0, upFlag)))
                return WindowDragFailure(commandId, ErrorCodes.WindowDragFailed,
                    "Windows rejected the mouse button up event.");

            buttonHeld = false;
        }
        finally
        {
            if (buttonHeld)
                ReleaseWindowClickButton(button);
        }

        Thread.Sleep(25);
        var finalRectangle = GetWindowRect(handle, out var currentRectangle)
            ? currentRectangle
            : initialRectangle;

        return new CommandResult<WindowDragResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new WindowDragResult
            {
                WindowHandle = FormatWindowHandle(handle),
                Title = title,
                ProcessId = processId,
                ProcessName = ReadProcessName((uint)processId),
                InitialBounds = ToWindowDragBounds(initialRectangle),
                FinalBounds = ToWindowDragBounds(finalRectangle),
                StartX = startX,
                StartY = startY,
                EndX = endX,
                EndY = endY,
                StartScreenX = startScreenX,
                StartScreenY = startScreenY,
                EndScreenX = endScreenX,
                EndScreenY = endScreenY,
                Button = button,
                DurationMs = durationMs,
                Steps = steps,
                WasMinimized = focusResult.Data.WasMinimized,
                Restored = focusResult.Data.Restored,
                PreviousForegroundWindow = focusResult.Data.PreviousForegroundWindow,
                IsForeground = GetForegroundWindow() == handle,
                Dragged = true
            }
        };
    }

    internal static bool TryTranslateWindowDragEndpoint(
        int left,
        int top,
        int x,
        int y,
        out int screenX,
        out int screenY)
    {
        try
        {
            screenX = checked(left + x);
            screenY = checked(top + y);
            return true;
        }
        catch (OverflowException)
        {
            screenX = 0;
            screenY = 0;
            return false;
        }
    }

    internal static int InterpolateWindowDragCoordinate(int start, int end, int step, int steps)
    {
        if (steps < 1 || step < 0 || step > steps)
            throw new ArgumentOutOfRangeException(nameof(step));

        return checked((int)(start + (((long)end - start) * step / steps)));
    }

    private static bool IsValidWindowDragStartCoordinate(int value) =>
        value is >= 0 and <= MaximumWindowDragCoordinate;

    private static bool IsValidWindowDragEndCoordinate(int value) =>
        value is >= -MaximumWindowDragCoordinate and <= MaximumWindowDragCoordinate;

    private static bool IsWindowDragPointOnVirtualDesktop(
        int x,
        int y,
        int virtualLeft,
        int virtualTop,
        int virtualWidth,
        int virtualHeight) =>
        virtualWidth > 0 && virtualHeight > 0 &&
        x >= virtualLeft && x < virtualLeft + virtualWidth &&
        y >= virtualTop && y < virtualTop + virtualHeight;

    [SupportedOSPlatform("windows")]
    private static bool SendWindowDragInput(NativeInput input)
    {
        var inputs = new[] { input };
        return SendInput(1, inputs, Marshal.SizeOf<NativeInput>()) == 1;
    }

    private static void WaitForWindowDragStep(
        Stopwatch stopwatch,
        int durationMs,
        int step,
        int steps,
        CancellationToken cancellationToken)
    {
        var targetElapsedMs = (long)durationMs * step / steps;
        while (stopwatch.ElapsedMilliseconds < targetElapsedMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = targetElapsedMs - stopwatch.ElapsedMilliseconds;
            var waitMs = (int)Math.Min(remaining, 25L);
            if (waitMs > 0 && cancellationToken.WaitHandle.WaitOne(waitMs))
                cancellationToken.ThrowIfCancellationRequested();
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static UiBounds ToWindowDragBounds(NativeRect rectangle) => new()
    {
        X = rectangle.Left,
        Y = rectangle.Top,
        Width = rectangle.Right - rectangle.Left,
        Height = rectangle.Bottom - rectangle.Top
    };

    private static CommandResult<WindowDragResult> WindowDragFailure(
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
