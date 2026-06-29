using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const int FocusVerificationAttempts = 10;
    private const int FocusVerificationDelayMilliseconds = 50;
    private const int ShowWindowRestore = 9;

    public async Task<CommandResult<WindowFocusResult>> FocusWindowAsync(
        string windowHandle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return FocusFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (!OperatingSystem.IsWindows())
            return FocusFailure(commandId, ErrorCodes.UiAutomationUnavailable, "Window focus is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => FocusWindowWindows(handle, commandId, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return FocusFailure(commandId, ErrorCodes.CommandCancelled, "The window focus request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation focus failure for command {CommandId}", commandId);
            return FocusFailure(commandId, ErrorCodes.WindowFocusFailed, "Windows UI Automation could not focus the requested window.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected window focus failure for command {CommandId}", commandId);
            return FocusFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while focusing the window.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<WindowFocusResult> FocusWindowWindows(
        IntPtr handle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return FocusFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        cancellationToken.ThrowIfCancellationRequested();
        var previousForegroundWindow = GetForegroundWindow();
        var wasMinimized = IsIconic(handle);
        var restored = false;
        IUIAutomation? automation = null;
        IUIAutomationElement? element = null;
        object? pattern = null;
        try
        {
            automation = new CUIAutomation8();
            element = automation.ElementFromHandle(handle);
            if (element is null)
                return FocusFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");

            if (wasMinimized)
            {
                pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_WindowPatternId);
                if (pattern is IUIAutomationWindowPattern windowPattern)
                    windowPattern.SetWindowVisualState(WindowVisualState.WindowVisualState_Normal);

                if (IsIconic(handle))
                    ShowWindowAsync(handle, ShowWindowRestore);

                restored = WaitForWindowState(
                    () => !IsIconic(handle),
                    cancellationToken);
                if (!restored)
                    return FocusFailure(commandId, ErrorCodes.WindowFocusFailed, "The requested window could not be restored from its minimized state.");
            }

            RequestForegroundActivation(handle);
            element.SetFocus();

            var isForeground = WaitForWindowState(
                () => GetForegroundWindow() == handle,
                cancellationToken);
            if (!isForeground)
            {
                SwitchToThisWindow(handle, true);
                RequestForegroundActivation(handle);
                element.SetFocus();
                isForeground = WaitForWindowState(
                    () => GetForegroundWindow() == handle,
                    cancellationToken);
            }

            if (!isForeground)
                return FocusFailure(commandId, ErrorCodes.WindowFocusFailed, "Windows did not grant foreground activation to the requested window.");

            return new CommandResult<WindowFocusResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new WindowFocusResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    PreviousForegroundWindow = FormatWindowHandle(previousForegroundWindow),
                    WasMinimized = wasMinimized,
                    Restored = restored,
                    IsForeground = true
                }
            };
        }
        finally
        {
            ReleaseComObject(pattern);
            ReleaseComObject(element);
            ReleaseComObject(automation);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RequestForegroundActivation(IntPtr handle)
    {
        var foregroundWindow = GetForegroundWindow();
        var currentThreadId = GetCurrentThreadId();
        var foregroundThreadId = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        var targetThreadId = GetWindowThreadProcessId(handle, out _);
        var attachedToForeground = false;
        var attachedToTarget = false;

        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                attachedToForeground = AttachThreadInput(currentThreadId, foregroundThreadId, true);

            if (targetThreadId != 0 &&
                targetThreadId != currentThreadId &&
                targetThreadId != foregroundThreadId)
            {
                attachedToTarget = AttachThreadInput(currentThreadId, targetThreadId, true);
            }

            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            SetActiveWindow(handle);
            NativeSetFocus(handle);
        }
        finally
        {
            if (attachedToTarget)
                AttachThreadInput(currentThreadId, targetThreadId, false);
            if (attachedToForeground)
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }
    }

    private static bool WaitForWindowState(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < FocusVerificationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
                return true;

            Thread.Sleep(FocusVerificationDelayMilliseconds);
        }

        return condition();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "SetFocus")]
    private static extern IntPtr NativeSetFocus(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(
        uint sourceThreadId,
        uint targetThreadId,
        [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.Bool)] bool altTab);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static CommandResult<WindowFocusResult> FocusFailure(Guid commandId, string code, string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };
}
