using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
namespace LocalMcp.Agent.Windows.UiAutomation;
public sealed partial class UiAutomationExecutor
{
    private const int MaximumWindowClickCoordinate = 100000;
    private const int MaximumWindowClickTitleLength = 1024;
    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventVirtualDesk = 0x4000;
    private const uint MouseEventAbsolute = 0x8000;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint GaRoot = 2;
    public async Task<CommandResult<WindowClickResult>> ClickWindowAsync(
        string windowHandle,
        int x,
        int y,
        string button,
        int clickCount,
        int? expectedProcessId,
        string? expectedWindowTitle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return WindowClickFailure(commandId, ErrorCodes.InvalidRequest, "Invalid windowHandle.");
        if (x is < 0 or > MaximumWindowClickCoordinate || y is < 0 or > MaximumWindowClickCoordinate)
            return WindowClickFailure(commandId, ErrorCodes.InvalidRequest, $"x and y must be between 0 and {MaximumWindowClickCoordinate}.");
        var normalizedButton = button?.Trim().ToLowerInvariant();
        if (!WindowMouseButtons.IsSupported(normalizedButton))
            return WindowClickFailure(commandId, ErrorCodes.InvalidRequest, "button must be left, right, or middle.");
        if (clickCount is < 1 or > 3)
            return WindowClickFailure(commandId, ErrorCodes.InvalidRequest, "clickCount must be between 1 and 3.");
        if (expectedProcessId is <= 0)
            return WindowClickFailure(commandId, ErrorCodes.InvalidRequest, "expectedProcessId must be greater than zero when provided.");
        if (expectedWindowTitle is not null &&
            (expectedWindowTitle.Length > MaximumWindowClickTitleLength || expectedWindowTitle.Any(char.IsControl)))
        {
            return WindowClickFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                $"expectedWindowTitle must be at most {MaximumWindowClickTitleLength} characters without control characters.");
        }
        if (!OperatingSystem.IsWindows())
            return WindowClickFailure(commandId, ErrorCodes.UiAutomationUnavailable, "Window coordinate clicking is only available on Windows agents.");
        try
        {
            return await Task.Run(
                () => ClickWindowWindows(
                    handle,
                    x,
                    y,
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
            return WindowClickFailure(commandId, ErrorCodes.CommandCancelled, "The window click request was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected window click failure for command {CommandId}", commandId);
            return WindowClickFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while clicking the window.");
        }
    }
    [SupportedOSPlatform("windows")]
    private static CommandResult<WindowClickResult> ClickWindowWindows(
        IntPtr handle,
        int x,
        int y,
        string button,
        int clickCount,
        int? expectedProcessId,
        string? expectedWindowTitle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return WindowClickFailure(commandId, ErrorCodes.WindowNotFound, "Window not found.");
        var guardError = ValidateWindowClickGuards(handle, expectedProcessId, expectedWindowTitle, out _, out _);
        if (guardError is not null)
            return WindowClickFailure(commandId, guardError.Code, guardError.Message);
        cancellationToken.ThrowIfCancellationRequested();
        var focusResult = FocusWindowWindows(handle, commandId, cancellationToken);
        if (!focusResult.Success || focusResult.Data is null)
        {
            return WindowClickFailure(
                commandId,
                focusResult.Error?.Code ?? ErrorCodes.WindowClickFailed,
                focusResult.Error?.Message ?? "The target window could not be focused before clicking.");
        }
        guardError = ValidateWindowClickGuards(
            handle,
            expectedProcessId,
            expectedWindowTitle,
            out var processId,
            out var title);
        if (guardError is not null)
            return WindowClickFailure(commandId, guardError.Code, guardError.Message);
        if (!GetWindowRect(handle, out var rectangle))
            return WindowClickFailure(commandId, ErrorCodes.WindowClickFailed, "The target window bounds could not be read.");
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (!TryTranslateWindowClickPoint(rectangle.Left, rectangle.Top, width, height, x, y, out var screenX, out var screenY))
        {
            return WindowClickFailure(
                commandId,
                ErrorCodes.WindowPointOutOfBounds,
                $"The local point ({x}, {y}) is outside the current window bounds {width}x{height}.");
        }
        var virtualLeft = GetSystemMetrics(SmXVirtualScreen);
        var virtualTop = GetSystemMetrics(SmYVirtualScreen);
        var virtualWidth = GetSystemMetrics(SmCxVirtualScreen);
        var virtualHeight = GetSystemMetrics(SmCyVirtualScreen);
        if (virtualWidth < 1 || virtualHeight < 1 ||
            screenX < virtualLeft || screenX >= virtualLeft + virtualWidth ||
            screenY < virtualTop || screenY >= virtualTop + virtualHeight)
        {
            return WindowClickFailure(commandId, ErrorCodes.WindowPointOutOfBounds, "The translated point is outside the Windows virtual desktop.");
        }
        var hitWindow = WindowFromPoint(new WindowClickPoint { X = screenX, Y = screenY });
        var hitRoot = hitWindow == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hitWindow, GaRoot);
        if (hitRoot != handle)
        {
            return WindowClickFailure(
                commandId,
                ErrorCodes.WindowClickFailed,
                "The requested point is currently covered by another window; no input was sent.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = BuildWindowClickInputs(
            screenX,
            screenY,
            virtualLeft,
            virtualTop,
            virtualWidth,
            virtualHeight,
            button,
            clickCount);
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (sent != inputs.Length)
        {
            ReleaseWindowClickButton(button);
            return WindowClickFailure(
                commandId,
                ErrorCodes.WindowClickFailed,
                $"SendInput accepted {sent} of {inputs.Length} mouse events.");
        }
        Thread.Sleep(25);
        var bounds = new UiBounds
        {
            X = rectangle.Left,
            Y = rectangle.Top,
            Width = width,
            Height = height
        };
        return new CommandResult<WindowClickResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new WindowClickResult
            {
                WindowHandle = FormatWindowHandle(handle),
                Title = title,
                ProcessId = processId,
                ProcessName = ReadProcessName((uint)processId),
                Bounds = bounds,
                X = x,
                Y = y,
                ScreenX = screenX,
                ScreenY = screenY,
                Button = button,
                ClickCount = clickCount,
                WasMinimized = focusResult.Data.WasMinimized,
                Restored = focusResult.Data.Restored,
                PreviousForegroundWindow = focusResult.Data.PreviousForegroundWindow,
                IsForeground = GetForegroundWindow() == handle,
                Clicked = true
            }
        };
    }
    [SupportedOSPlatform("windows")]
    private static CommandError? ValidateWindowClickGuards(
        IntPtr handle,
        int? expectedProcessId,
        string? expectedWindowTitle,
        out int processId,
        out string title)
    {
        GetWindowThreadProcessId(handle, out var rawProcessId);
        processId = rawProcessId <= int.MaxValue ? (int)rawProcessId : 0;
        title = ReadWindowTitle(handle);
        if (expectedProcessId.HasValue && processId != expectedProcessId.Value)
        {
            return new CommandError(
                ErrorCodes.WindowGuardMismatch,
                $"The window process id changed: expected {expectedProcessId.Value}, actual {processId}.");
        }
        if (expectedWindowTitle is not null &&
            !string.Equals(title, expectedWindowTitle, StringComparison.OrdinalIgnoreCase))
        {
            return new CommandError(
                ErrorCodes.WindowGuardMismatch,
                "The window title no longer matches expectedWindowTitle.");
        }
        return null;
    }
    internal static bool TryTranslateWindowClickPoint(
        int left,
        int top,
        int width,
        int height,
        int x,
        int y,
        out int screenX,
        out int screenY)
    {
        screenX = 0;
        screenY = 0;
        if (width < 1 || height < 1 || x < 0 || y < 0 || x >= width || y >= height)
            return false;
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
    internal static int NormalizeWindowClickCoordinate(int coordinate, int origin, int length)
    {
        if (length <= 1)
            return 0;
        var relative = Math.Clamp((long)coordinate - origin, 0, length - 1L);
        return checked((int)((relative * 65535L + (length - 2L) / 2L) / (length - 1L)));
    }
    private static NativeInput[] BuildWindowClickInputs(
        int screenX,
        int screenY,
        int virtualLeft,
        int virtualTop,
        int virtualWidth,
        int virtualHeight,
        string button,
        int clickCount)
    {
        var (downFlag, upFlag) = GetWindowClickButtonFlags(button);
        var inputs = new NativeInput[1 + clickCount * 2];
        inputs[0] = BuildWindowMouseInput(
            NormalizeWindowClickCoordinate(screenX, virtualLeft, virtualWidth),
            NormalizeWindowClickCoordinate(screenY, virtualTop, virtualHeight),
            MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk);
        for (var click = 0; click < clickCount; click++)
        {
            inputs[1 + click * 2] = BuildWindowMouseInput(0, 0, downFlag);
            inputs[2 + click * 2] = BuildWindowMouseInput(0, 0, upFlag);
        }
        return inputs;
    }
    private static NativeInput BuildWindowMouseInput(int x, int y, uint flags) => new()
    {
        Type = InputMouse,
        Data = new NativeInputUnion
        {
            Mouse = new NativeMouseInput
            {
                X = x,
                Y = y,
                Flags = flags
            }
        }
    };
    private static (uint Down, uint Up) GetWindowClickButtonFlags(string button) => button switch
    {
        WindowMouseButtons.Left => (MouseEventLeftDown, MouseEventLeftUp),
        WindowMouseButtons.Right => (MouseEventRightDown, MouseEventRightUp),
        WindowMouseButtons.Middle => (MouseEventMiddleDown, MouseEventMiddleUp),
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };
    [SupportedOSPlatform("windows")]
    private static void ReleaseWindowClickButton(string button)
    {
        var (_, upFlag) = GetWindowClickButtonFlags(button);
        var releases = new[] { BuildWindowMouseInput(0, 0, upFlag) };
        SendInput(1, releases, Marshal.SizeOf<NativeInput>());
    }
    private static CommandResult<WindowClickResult> WindowClickFailure(
        Guid commandId,
        string code,
        string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClickPoint
    {
        public int X;
        public int Y;
    }
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(WindowClickPoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);
}
