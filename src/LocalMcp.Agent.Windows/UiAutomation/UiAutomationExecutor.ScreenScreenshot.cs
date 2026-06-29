using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const uint MonitorInfoPrimary = 0x00000001;
    private const int EffectiveDpi = 0;
    private const int LogicalPixelsX = 88;
    private const int LogicalPixelsY = 90;

    public async Task<CommandResult<ScreenScreenshotResult>> CaptureScreenScreenshotAsync(
        int? monitorIndex,
        int? x,
        int? y,
        int? width,
        int? height,
        int maxWidth,
        int maxHeight,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var hasAnyRegionValue = x.HasValue || y.HasValue || width.HasValue || height.HasValue;
        var hasCompleteRegion = x.HasValue && y.HasValue && width.HasValue && height.HasValue;

        if (maxWidth is < 1 or > MaximumScreenshotDimension ||
            maxHeight is < 1 or > MaximumScreenshotDimension)
        {
            return ScreenScreenshotFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                $"maxWidth and maxHeight must be between 1 and {MaximumScreenshotDimension}.");
        }

        if (monitorIndex is < 0)
            return ScreenScreenshotFailure(commandId, ErrorCodes.InvalidRequest, "monitorIndex must be zero or greater.");
        if (monitorIndex.HasValue && hasAnyRegionValue)
            return ScreenScreenshotFailure(commandId, ErrorCodes.InvalidRequest, "monitorIndex and region coordinates cannot be combined.");
        if (hasAnyRegionValue && !hasCompleteRegion)
            return ScreenScreenshotFailure(commandId, ErrorCodes.InvalidRequest, "x, y, width, and height must all be supplied for region capture.");
        if (width is <= 0 || height is <= 0)
            return ScreenScreenshotFailure(commandId, ErrorCodes.InvalidRequest, "Region width and height must be greater than zero.");
        if (hasCompleteRegion &&
            ((long)x!.Value + width!.Value > int.MaxValue ||
             (long)y!.Value + height!.Value > int.MaxValue))
        {
            return ScreenScreenshotFailure(commandId, ErrorCodes.InvalidRequest, "The requested region coordinates exceed the supported range.");
        }
        if (!OperatingSystem.IsWindows())
            return ScreenScreenshotFailure(commandId, ErrorCodes.UiAutomationUnavailable, "Screen screenshots are only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => CaptureScreenScreenshotWindows(
                    monitorIndex,
                    x,
                    y,
                    width,
                    height,
                    maxWidth,
                    maxHeight,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ScreenScreenshotFailure(commandId, ErrorCodes.CommandCancelled, "The screen screenshot request was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected screen screenshot failure for command {CommandId}", commandId);
            return ScreenScreenshotFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while capturing the desktop.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<ScreenScreenshotResult> CaptureScreenScreenshotWindows(
        int? monitorIndex,
        int? x,
        int? y,
        int? width,
        int? height,
        int maxWidth,
        int maxHeight,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previousDpiContext = TrySetPerMonitorDpiAwareness();
        try
        {
            var monitors = EnumerateScreenMonitors();
            if (monitors.Count == 0)
                return ScreenScreenshotFailure(commandId, ErrorCodes.ScreenScreenshotFailed, "Windows did not report any active monitors.");

            var virtualBounds = ReadVirtualScreenBounds(monitors);
            if (!TryResolveCaptureBounds(
                    monitors,
                    virtualBounds,
                    monitorIndex,
                    x,
                    y,
                    width,
                    height,
                    out var captureBounds,
                    out var captureMode,
                    out var selectedMonitorIndex,
                    out var validationMessage))
            {
                return ScreenScreenshotFailure(commandId, ErrorCodes.InvalidRequest, validationMessage!);
            }

            if (captureBounds.Width > MaximumSourceDimension || captureBounds.Height > MaximumSourceDimension ||
                (long)captureBounds.Width * captureBounds.Height > MaximumSourcePixels)
            {
                return ScreenScreenshotFailure(commandId, ErrorCodes.ScreenScreenshotTooLarge, "The source desktop region is too large to capture safely.");
            }

            var capture = CaptureDesktopBgra(captureBounds, cancellationToken);
            if (capture.Pixels is null)
                return ScreenScreenshotFailure(commandId, ErrorCodes.ScreenScreenshotFailed, capture.ErrorMessage!);

            var outputWidth = 0;
            var outputHeight = 0;
            (outputWidth, outputHeight) = FitDimensions(captureBounds.Width, captureBounds.Height, maxWidth, maxHeight);
            var png = EncodeWithinLimit(
                capture.Pixels,
                captureBounds.Width,
                captureBounds.Height,
                ref outputWidth,
                ref outputHeight,
                cancellationToken);
            if (png is null)
                return ScreenScreenshotFailure(commandId, ErrorCodes.ScreenScreenshotTooLarge, "The PNG screenshot exceeds the safe transport limit.");

            var sha256 = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            return new CommandResult<ScreenScreenshotResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new ScreenScreenshotResult
                {
                    CaptureMode = captureMode!,
                    SelectedMonitorIndex = selectedMonitorIndex,
                    Bounds = captureBounds.ToUiBounds(),
                    VirtualScreenBounds = virtualBounds.ToUiBounds(),
                    Monitors = monitors.Select(static monitor => monitor.ToResult()).ToArray(),
                    OriginalWidth = captureBounds.Width,
                    OriginalHeight = captureBounds.Height,
                    Width = outputWidth,
                    Height = outputHeight,
                    Scaled = outputWidth != captureBounds.Width || outputHeight != captureBounds.Height,
                    CaptureMethod = "screen-copy",
                    ByteLength = png.Length,
                    Sha256 = sha256,
                    PngBase64 = Convert.ToBase64String(png)
                }
            };
        }
        finally
        {
            RestoreDpiAwareness(previousDpiContext);
        }
    }

    internal static bool TryResolveCaptureBounds(
        IReadOnlyList<ScreenMonitorSnapshot> monitors,
        ScreenCaptureBounds virtualBounds,
        int? monitorIndex,
        int? x,
        int? y,
        int? width,
        int? height,
        out ScreenCaptureBounds captureBounds,
        out string? captureMode,
        out int? selectedMonitorIndex,
        out string? errorMessage)
    {
        captureBounds = default;
        captureMode = null;
        selectedMonitorIndex = null;
        errorMessage = null;

        if (monitorIndex.HasValue)
        {
            if (monitorIndex.Value >= monitors.Count)
            {
                errorMessage = $"monitorIndex must be between 0 and {monitors.Count - 1}.";
                return false;
            }

            var monitor = monitors[monitorIndex.Value];
            captureBounds = monitor.Bounds;
            captureMode = "monitor";
            selectedMonitorIndex = monitor.Index;
            return true;
        }

        if (x.HasValue)
        {
            var requested = new ScreenCaptureBounds(x.Value, y!.Value, width!.Value, height!.Value);
            if (!virtualBounds.Contains(requested))
            {
                errorMessage = "The requested region must be fully contained inside the virtual desktop bounds.";
                return false;
            }

            captureBounds = requested;
            captureMode = "region";
            selectedMonitorIndex = FindBestMonitor(monitors, requested)?.Index;
            return true;
        }

        captureBounds = virtualBounds;
        captureMode = "virtual-screen";
        return true;
    }

    private static ScreenMonitorSnapshot? FindBestMonitor(
        IReadOnlyList<ScreenMonitorSnapshot> monitors,
        ScreenCaptureBounds requested) =>
        monitors
            .Select(monitor => new { Monitor = monitor, Area = monitor.Bounds.IntersectionArea(requested) })
            .OrderByDescending(static item => item.Area)
            .ThenBy(static item => item.Monitor.Index)
            .FirstOrDefault(static item => item.Area > 0)
            ?.Monitor;

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<ScreenMonitorSnapshot> EnumerateScreenMonitors()
    {
        var raw = new List<ScreenMonitorSnapshot>();
        ScreenMonitorEnumProc callback = (
            IntPtr monitorHandle,
            IntPtr monitorDc,
            ref ScreenNativeRect monitorRect,
            IntPtr data) =>
        {
            _ = monitorDc;
            _ = monitorRect;
            _ = data;
            var info = new ScreenMonitorInfoNative
            {
                Size = checked((uint)Marshal.SizeOf<ScreenMonitorInfoNative>()),
                DeviceName = string.Empty
            };
            if (!ScreenGetMonitorInfo(monitorHandle, ref info))
                return true;

            ReadMonitorDpi(monitorHandle, info.DeviceName, out var dpiX, out var dpiY);
            raw.Add(new ScreenMonitorSnapshot(
                Index: 0,
                DeviceName: info.DeviceName ?? string.Empty,
                IsPrimary: (info.Flags & MonitorInfoPrimary) != 0,
                Bounds: ScreenCaptureBounds.FromRect(info.Monitor),
                WorkArea: ScreenCaptureBounds.FromRect(info.WorkArea),
                DpiX: dpiX,
                DpiY: dpiY));
            return true;
        };

        if (!ScreenEnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            return [];

        return raw
            .OrderByDescending(static monitor => monitor.IsPrimary)
            .ThenBy(static monitor => monitor.Bounds.Y)
            .ThenBy(static monitor => monitor.Bounds.X)
            .Select(static (monitor, index) => monitor with { Index = index })
            .ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static ScreenCaptureBounds ReadVirtualScreenBounds(IReadOnlyList<ScreenMonitorSnapshot> monitors)
    {
        var width = ScreenGetSystemMetrics(SmCxVirtualScreen);
        var height = ScreenGetSystemMetrics(SmCyVirtualScreen);
        if (width > 0 && height > 0)
        {
            return new ScreenCaptureBounds(
                ScreenGetSystemMetrics(SmXVirtualScreen),
                ScreenGetSystemMetrics(SmYVirtualScreen),
                width,
                height);
        }

        var left = monitors.Min(static monitor => monitor.Bounds.X);
        var top = monitors.Min(static monitor => monitor.Bounds.Y);
        var right = monitors.Max(static monitor => monitor.Bounds.Right);
        var bottom = monitors.Max(static monitor => monitor.Bounds.Bottom);
        return new ScreenCaptureBounds(left, top, right - left, bottom - top);
    }

    [SupportedOSPlatform("windows")]
    private static ScreenCapture CaptureDesktopBgra(
        ScreenCaptureBounds bounds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var desktopDc = ScreenshotGetDc(IntPtr.Zero);
        if (desktopDc == IntPtr.Zero)
            return ScreenCapture.Failure("A desktop device context could not be created.");

        var memoryDc = ScreenshotCreateCompatibleDc(desktopDc);
        if (memoryDc == IntPtr.Zero)
        {
            ScreenshotReleaseDc(IntPtr.Zero, desktopDc);
            return ScreenCapture.Failure("A memory device context could not be created.");
        }

        var bitmapInfo = new ScreenshotBitmapInfo
        {
            Header = new ScreenshotBitmapInfoHeader
            {
                Size = checked((uint)Marshal.SizeOf<ScreenshotBitmapInfoHeader>()),
                Width = bounds.Width,
                Height = -bounds.Height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
                SizeImage = checked((uint)(bounds.Width * bounds.Height * 4))
            }
        };

        var bitmap = ScreenshotCreateDibSection(
            desktopDc,
            ref bitmapInfo,
            DibRgbColors,
            out var bits,
            IntPtr.Zero,
            0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            ScreenshotDeleteDc(memoryDc);
            ScreenshotReleaseDc(IntPtr.Zero, desktopDc);
            return ScreenCapture.Failure("A screenshot bitmap could not be allocated.");
        }

        var previous = ScreenshotSelectObject(memoryDc, bitmap);
        try
        {
            if (!ScreenshotBitBlt(
                    memoryDc,
                    0,
                    0,
                    bounds.Width,
                    bounds.Height,
                    desktopDc,
                    bounds.X,
                    bounds.Y,
                    SourceCopy | CaptureBlt))
            {
                return ScreenCapture.Failure("Windows could not copy the requested desktop region.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return ScreenCapture.Success(pixels);
        }
        finally
        {
            if (previous != IntPtr.Zero)
                ScreenshotSelectObject(memoryDc, previous);
            ScreenshotDeleteObject(bitmap);
            ScreenshotDeleteDc(memoryDc);
            ScreenshotReleaseDc(IntPtr.Zero, desktopDc);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadMonitorDpi(IntPtr monitorHandle, string? deviceName, out uint dpiX, out uint dpiY)
    {
        dpiX = 96;
        dpiY = 96;
        try
        {
            if (ScreenGetDpiForMonitor(monitorHandle, EffectiveDpi, out dpiX, out dpiY) == 0 && dpiX > 0 && dpiY > 0)
                return;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        var dc = ScreenCreateDc("DISPLAY", deviceName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero)
            return;
        try
        {
            var fallbackX = ScreenGetDeviceCaps(dc, LogicalPixelsX);
            var fallbackY = ScreenGetDeviceCaps(dc, LogicalPixelsY);
            if (fallbackX > 0)
                dpiX = checked((uint)fallbackX);
            if (fallbackY > 0)
                dpiY = checked((uint)fallbackY);
        }
        finally
        {
            ScreenDeleteDc(dc);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr TrySetPerMonitorDpiAwareness()
    {
        try
        {
            return ScreenSetThreadDpiAwarenessContext(new IntPtr(-4));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreDpiAwareness(IntPtr previousContext)
    {
        if (previousContext == IntPtr.Zero)
            return;
        try
        {
            ScreenSetThreadDpiAwarenessContext(previousContext);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static CommandResult<ScreenScreenshotResult> ScreenScreenshotFailure(
        Guid commandId,
        string code,
        string message) =>
        new() { CommandId = commandId, Success = false, Error = new CommandError(code, message) };

    internal readonly record struct ScreenCaptureBounds(int X, int Y, int Width, int Height)
    {
        public int Right => checked(X + Width);
        public int Bottom => checked(Y + Height);

        public bool Contains(ScreenCaptureBounds other) =>
            other.Width > 0 && other.Height > 0 &&
            other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

        public long IntersectionArea(ScreenCaptureBounds other)
        {
            var left = Math.Max(X, other.X);
            var top = Math.Max(Y, other.Y);
            var right = Math.Min(Right, other.Right);
            var bottom = Math.Min(Bottom, other.Bottom);
            return right > left && bottom > top ? (long)(right - left) * (bottom - top) : 0;
        }

        public UiBounds ToUiBounds() => new() { X = X, Y = Y, Width = Width, Height = Height };
        public static ScreenCaptureBounds FromRect(ScreenNativeRect rect) =>
            new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    internal sealed record ScreenMonitorSnapshot(
        int Index,
        string DeviceName,
        bool IsPrimary,
        ScreenCaptureBounds Bounds,
        ScreenCaptureBounds WorkArea,
        uint DpiX,
        uint DpiY)
    {
        public ScreenMonitorInfo ToResult() => new()
        {
            Index = Index,
            DeviceName = DeviceName,
            IsPrimary = IsPrimary,
            Bounds = Bounds.ToUiBounds(),
            WorkArea = WorkArea.ToUiBounds(),
            DpiX = DpiX,
            DpiY = DpiY,
            ScaleFactor = Math.Round(DpiX / 96d, 4)
        };
    }

    private sealed record ScreenCapture(byte[]? Pixels, string? ErrorMessage)
    {
        public static ScreenCapture Success(byte[] pixels) => new(pixels, null);
        public static ScreenCapture Failure(string message) => new(null, message);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ScreenNativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ScreenMonitorInfoNative
    {
        public uint Size;
        public ScreenNativeRect Monitor;
        public ScreenNativeRect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool ScreenMonitorEnumProc(
        IntPtr monitorHandle,
        IntPtr monitorDc,
        ref ScreenNativeRect monitorRect,
        IntPtr data);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayMonitors", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenEnumDisplayMonitors(
        IntPtr dc,
        IntPtr clipRect,
        ScreenMonitorEnumProc callback,
        IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenGetMonitorInfo(IntPtr monitorHandle, ref ScreenMonitorInfoNative monitorInfo);

    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static extern int ScreenGetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "SetThreadDpiAwarenessContext")]
    private static extern IntPtr ScreenSetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("shcore.dll", EntryPoint = "GetDpiForMonitor")]
    private static extern int ScreenGetDpiForMonitor(IntPtr monitorHandle, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode)]
    private static extern IntPtr ScreenCreateDc(string driver, string? device, string? output, IntPtr initData);

    [DllImport("gdi32.dll", EntryPoint = "GetDeviceCaps")]
    private static extern int ScreenGetDeviceCaps(IntPtr dc, int index);

    [DllImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenDeleteDc(IntPtr dc);

}
