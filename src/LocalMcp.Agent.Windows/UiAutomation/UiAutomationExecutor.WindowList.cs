using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const int MaxWindowTitleCharacters = 1024;
    private const int MaxClassNameCharacters = 256;
    private const int DwmwaCloaked = 14;

    public async Task<CommandResult<WindowListResult>> ListWindowsAsync(
        bool includeInvisible,
        bool includeUntitled,
        int maxWindows,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (maxWindows is < 1 or > 500)
            return WindowListFailure(commandId, ErrorCodes.InvalidRequest, "maxWindows must be between 1 and 500.");
        if (!OperatingSystem.IsWindows())
            return WindowListFailure(commandId, ErrorCodes.UiAutomationUnavailable, "Window enumeration is only available on Windows agents.");

        try
        {
            return await RunWindowListAsync(
                includeInvisible,
                includeUntitled,
                maxWindows,
                commandId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return WindowListFailure(commandId, ErrorCodes.CommandCancelled, "The window list request was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected window enumeration failure for command {CommandId}", commandId);
            return WindowListFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while listing windows.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static Task<CommandResult<WindowListResult>> RunWindowListAsync(
        bool includeInvisible,
        bool includeUntitled,
        int maxWindows,
        Guid commandId,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => ListWindowsWindows(
                includeInvisible,
                includeUntitled,
                maxWindows,
                commandId,
                cancellationToken),
            cancellationToken);

    [SupportedOSPlatform("windows")]
    private static CommandResult<WindowListResult> ListWindowsWindows(
        bool includeInvisible,
        bool includeUntitled,
        int maxWindows,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var snapshot = CaptureWindowSnapshot(
            includeInvisible,
            includeUntitled,
            cancellationToken);
        if (!snapshot.Completed)
        {
            return WindowListFailure(
                commandId,
                ErrorCodes.WindowEnumerationFailed,
                "Windows could not enumerate top-level windows.");
        }

        var truncated = snapshot.Windows.Count > maxWindows;
        var windows = snapshot.Windows.Take(maxWindows).ToList();
        return new CommandResult<WindowListResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new WindowListResult
            {
                Windows = windows,
                Count = windows.Count,
                MaxWindows = maxWindows,
                Truncated = truncated
            }
        };
    }

    [SupportedOSPlatform("windows")]
    private static WindowSnapshot CaptureWindowSnapshot(
        bool includeInvisible,
        bool includeUntitled,
        CancellationToken cancellationToken)
    {
        var windows = new List<WindowInfo>();
        var foregroundWindow = GetForegroundWindow();
        var cancelled = false;
        var zOrder = 0;

        EnumWindowsProc callback = (windowHandle, parameter) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                return false;
            }

            var currentZOrder = zOrder++;
            if (!IsWindow(windowHandle))
                return true;

            var isVisible = IsWindowVisible(windowHandle);
            var isCloaked = IsWindowCloaked(windowHandle);
            if (!includeInvisible && (!isVisible || isCloaked))
                return true;

            var title = ReadWindowTitle(windowHandle);
            if (!includeUntitled && string.IsNullOrWhiteSpace(title))
                return true;

            GetWindowThreadProcessId(windowHandle, out var processId);
            windows.Add(new WindowInfo
            {
                WindowHandle = FormatWindowHandle(windowHandle),
                WindowHandleDecimal = unchecked((ulong)windowHandle.ToInt64())
                    .ToString(CultureInfo.InvariantCulture),
                Title = title,
                ProcessId = unchecked((int)processId),
                ProcessName = ReadProcessName(processId),
                ClassName = ReadClassName(windowHandle),
                Bounds = ReadWindowBounds(windowHandle),
                IsVisible = isVisible,
                IsEnabled = IsWindowEnabled(windowHandle),
                IsMinimized = IsIconic(windowHandle),
                IsMaximized = IsZoomed(windowHandle),
                IsForeground = windowHandle == foregroundWindow,
                IsCloaked = isCloaked,
                ZOrder = currentZOrder
            });

            return true;
        };

        var completed = EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        if (cancelled)
            cancellationToken.ThrowIfCancellationRequested();

        var ordered = windows
            .OrderByDescending(window => window.IsForeground)
            .ThenBy(window => window.ZOrder)
            .ToList();
        return new WindowSnapshot(completed, ordered);
    }

    [SupportedOSPlatform("windows")]
    private static string ReadWindowTitle(IntPtr windowHandle)
    {
        var builder = new StringBuilder(MaxWindowTitleCharacters + 1);
        var length = GetWindowText(windowHandle, builder, builder.Capacity);
        return length <= 0 ? string.Empty : builder.ToString(0, length);
    }

    [SupportedOSPlatform("windows")]
    private static string ReadClassName(IntPtr windowHandle)
    {
        var builder = new StringBuilder(MaxClassNameCharacters + 1);
        var length = GetClassName(windowHandle, builder, builder.Capacity);
        return length <= 0 ? string.Empty : builder.ToString(0, length);
    }

    private static string ReadProcessName(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue)
            return string.Empty;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;
            return processName.Length <= 256 ? processName : processName[..256];
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return string.Empty;
        }
    }

    [SupportedOSPlatform("windows")]
    private static UiBounds ReadWindowBounds(IntPtr windowHandle)
    {
        if (!GetWindowRect(windowHandle, out var rectangle))
            return new UiBounds();

        return new UiBounds
        {
            X = rectangle.Left,
            Y = rectangle.Top,
            Width = Math.Max(0, rectangle.Right - rectangle.Left),
            Height = Math.Max(0, rectangle.Bottom - rectangle.Top)
        };
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowCloaked(IntPtr windowHandle)
    {
        try
        {
            var result = DwmGetWindowAttribute(
                windowHandle,
                DwmwaCloaked,
                out var cloaked,
                Marshal.SizeOf<int>());
            return result == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static CommandResult<WindowListResult> WindowListFailure(
        Guid commandId,
        string code,
        string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };

    private sealed record WindowSnapshot(bool Completed, IReadOnlyList<WindowInfo> Windows);

    private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(
        IntPtr windowHandle,
        StringBuilder text,
        int maximumCharacters);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(
        IntPtr windowHandle,
        StringBuilder className,
        int maximumCharacters);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr windowHandle,
        out NativeRect rectangle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        out int attributeValue,
        int attributeSize);
}
