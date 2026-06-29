using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const int MaximumScreenshotDimension = 4096;
    private const int MaximumSourceDimension = 16384;
    private const long MaximumSourcePixels = 67_108_864;
    private const int MaximumScreenshotPngBytes = 6 * 1024 * 1024;
    private const uint PwRenderFullContent = 0x00000002;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const int ShowWindowNoActivate = 4;
    private const int ShowWindowMinNoActivate = 7;
    private const int ScreenshotRestoreDelayMilliseconds = 150;

    public async Task<CommandResult<WindowScreenshotResult>> CaptureWindowScreenshotAsync(
        string windowHandle,
        int maxWidth,
        int maxHeight,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return ScreenshotFailure(commandId, ErrorCodes.InvalidRequest, "Invalid windowHandle.");
        if (maxWidth is < 1 or > MaximumScreenshotDimension ||
            maxHeight is < 1 or > MaximumScreenshotDimension)
        {
            return ScreenshotFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                $"maxWidth and maxHeight must be between 1 and {MaximumScreenshotDimension}.");
        }
        if (!OperatingSystem.IsWindows())
            return ScreenshotFailure(commandId, ErrorCodes.UiAutomationUnavailable, "Window screenshots are only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => CaptureWindowScreenshotWindows(
                    handle,
                    maxWidth,
                    maxHeight,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ScreenshotFailure(commandId, ErrorCodes.CommandCancelled, "The window screenshot request was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected window screenshot failure for command {CommandId}", commandId);
            return ScreenshotFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while capturing the window.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<WindowScreenshotResult> CaptureWindowScreenshotWindows(
        IntPtr handle,
        int maxWidth,
        int maxHeight,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return ScreenshotFailure(commandId, ErrorCodes.WindowNotFound, "Window not found.");
        var wasMinimized = IsIconic(handle);
        if (!TryGetScreenshotRectangle(handle, wasMinimized, out var rectangle))
            return ScreenshotFailure(commandId, ErrorCodes.WindowScreenshotFailed, "The window bounds could not be read.");

        var sourceWidth = rectangle.Right - rectangle.Left;
        var sourceHeight = rectangle.Bottom - rectangle.Top;
        if (sourceWidth < 1 || sourceHeight < 1)
            return ScreenshotFailure(commandId, ErrorCodes.WindowScreenshotFailed, "The window has empty bounds.");
        if (sourceWidth > MaximumSourceDimension || sourceHeight > MaximumSourceDimension ||
            (long)sourceWidth * sourceHeight > MaximumSourcePixels)
        {
            return ScreenshotFailure(commandId, ErrorCodes.WindowScreenshotTooLarge, "The source window is too large to capture safely.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var capture = CaptureBgra(
            handle,
            rectangle,
            sourceWidth,
            sourceHeight,
            wasMinimized,
            cancellationToken);
        if (capture.Pixels is null)
            return ScreenshotFailure(commandId, ErrorCodes.WindowScreenshotFailed, capture.ErrorMessage!);

        var (outputWidth, outputHeight) = FitDimensions(sourceWidth, sourceHeight, maxWidth, maxHeight);
        var png = EncodeWithinLimit(
            capture.Pixels,
            sourceWidth,
            sourceHeight,
            ref outputWidth,
            ref outputHeight,
            cancellationToken);
        if (png is null)
            return ScreenshotFailure(commandId, ErrorCodes.WindowScreenshotTooLarge, "The PNG screenshot exceeds the safe transport limit.");

        GetWindowThreadProcessId(handle, out var rawProcessId);
        var processId = rawProcessId <= int.MaxValue ? (int)rawProcessId : 0;
        var sha256 = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        var bounds = new UiBounds
        {
            X = rectangle.Left,
            Y = rectangle.Top,
            Width = sourceWidth,
            Height = sourceHeight
        };

        return new CommandResult<WindowScreenshotResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new WindowScreenshotResult
            {
                WindowHandle = FormatWindowHandle(handle),
                Title = ReadWindowTitle(handle),
                ProcessId = processId,
                ProcessName = ReadProcessName(rawProcessId),
                Bounds = bounds,
                OriginalWidth = sourceWidth,
                OriginalHeight = sourceHeight,
                Width = outputWidth,
                Height = outputHeight,
                Scaled = outputWidth != sourceWidth || outputHeight != sourceHeight,
                WasMinimized = wasMinimized,
                CaptureMethod = capture.Method!,
                ByteLength = png.Length,
                Sha256 = sha256,
                PngBase64 = Convert.ToBase64String(png)
            }
        };
    }

    private static byte[]? EncodeWithinLimit(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        ref int outputWidth,
        ref int outputHeight,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pixels = outputWidth == sourceWidth && outputHeight == sourceHeight
                ? source
                : ScaleBgra(source, sourceWidth, sourceHeight, outputWidth, outputHeight, cancellationToken);
            var png = BgraPngEncoder.Encode(pixels, outputWidth, outputHeight);
            if (png.Length <= MaximumScreenshotPngBytes)
                return png;

            if (outputWidth <= 320 && outputHeight <= 320)
                return null;

            outputWidth = Math.Max(1, (int)Math.Floor(outputWidth * 0.8));
            outputHeight = Math.Max(1, (int)Math.Floor(outputHeight * 0.8));
        }

        return null;
    }

    private static byte[] ScaleBgra(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken)
    {
        var destination = new byte[checked(targetWidth * targetHeight * 4)];
        for (var y = 0; y < targetHeight; y++)
        {
            if ((y & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var sourceY = (int)((long)y * sourceHeight / targetHeight);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = (int)((long)x * sourceWidth / targetWidth);
                var sourceOffset = (sourceY * sourceWidth + sourceX) * 4;
                var destinationOffset = (y * targetWidth + x) * 4;
                Buffer.BlockCopy(source, sourceOffset, destination, destinationOffset, 4);
            }
        }
        return destination;
    }

    private static (int Width, int Height) FitDimensions(
        int width,
        int height,
        int maxWidth,
        int maxHeight)
    {
        var scale = Math.Min(1d, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        return (
            Math.Max(1, (int)Math.Floor(width * scale)),
            Math.Max(1, (int)Math.Floor(height * scale)));
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetScreenshotRectangle(
        IntPtr handle,
        bool wasMinimized,
        out NativeRect rectangle)
    {
        if (!GetWindowRect(handle, out rectangle))
            return false;
        if (!wasMinimized)
            return true;

        var placement = new ScreenshotWindowPlacement
        {
            Length = checked((uint)Marshal.SizeOf<ScreenshotWindowPlacement>())
        };
        if (!ScreenshotGetWindowPlacement(handle, ref placement))
            return true;

        var normal = placement.NormalPosition;
        if (normal.Right > normal.Left && normal.Bottom > normal.Top)
            rectangle = normal;
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static ScreenshotCapture CaptureBgra(
        IntPtr handle,
        NativeRect rectangle,
        int width,
        int height,
        bool wasMinimized,
        CancellationToken cancellationToken)
    {
        var restoredForCapture = false;
        try
        {
            if (wasMinimized)
            {
                ShowWindowAsync(handle, ShowWindowNoActivate);
                if (!WaitForWindowState(() => !IsIconic(handle), cancellationToken))
                    return ScreenshotCapture.Failure("The minimized window could not be restored for capture.");

                restoredForCapture = true;
                Thread.Sleep(ScreenshotRestoreDelayMilliseconds);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var capture = CaptureBgraCore(handle, rectangle, width, height);
            if (capture.Pixels is not null && restoredForCapture)
                return ScreenshotCapture.Success(capture.Pixels, $"{capture.Method}-restored");

            return capture;
        }
        finally
        {
            if (restoredForCapture && IsWindow(handle))
            {
                ShowWindowAsync(handle, ShowWindowMinNoActivate);
                WaitForWindowState(() => IsIconic(handle), CancellationToken.None);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static ScreenshotCapture CaptureBgraCore(
        IntPtr handle,
        NativeRect rectangle,
        int width,
        int height)
    {
        var desktopDc = ScreenshotGetDc(IntPtr.Zero);
        if (desktopDc == IntPtr.Zero)
            return ScreenshotCapture.Failure("A desktop device context could not be created.");

        var memoryDc = ScreenshotCreateCompatibleDc(desktopDc);
        if (memoryDc == IntPtr.Zero)
        {
            ScreenshotReleaseDc(IntPtr.Zero, desktopDc);
            return ScreenshotCapture.Failure("A memory device context could not be created.");
        }

        var bitmapInfo = new ScreenshotBitmapInfo
        {
            Header = new ScreenshotBitmapInfoHeader
            {
                Size = checked((uint)Marshal.SizeOf<ScreenshotBitmapInfoHeader>()),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
                SizeImage = checked((uint)(width * height * 4))
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
            return ScreenshotCapture.Failure("A screenshot bitmap could not be allocated.");
        }

        var previous = ScreenshotSelectObject(memoryDc, bitmap);
        try
        {
            var captured = ScreenshotPrintWindow(handle, memoryDc, PwRenderFullContent);
            var method = "print-window";
            if (!captured)
            {
                captured = ScreenshotBitBlt(
                    memoryDc,
                    0,
                    0,
                    width,
                    height,
                    desktopDc,
                    rectangle.Left,
                    rectangle.Top,
                    SourceCopy | CaptureBlt);
                method = "screen-copy";
            }

            if (!captured)
                return ScreenshotCapture.Failure("Windows could not render the requested window.");

            var pixels = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return ScreenshotCapture.Success(pixels, method);
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

    private static CommandResult<WindowScreenshotResult> ScreenshotFailure(
        Guid commandId,
        string code,
        string message) =>
        new() { CommandId = commandId, Success = false, Error = new CommandError(code, message) };

    private sealed record ScreenshotCapture(byte[]? Pixels, string? Method, string? ErrorMessage)
    {
        public static ScreenshotCapture Success(byte[] pixels, string method) => new(pixels, method, null);
        public static ScreenshotCapture Failure(string message) => new(null, null, message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenshotPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenshotWindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCommand;
        public ScreenshotPoint MinPosition;
        public ScreenshotPoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenshotBitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenshotBitmapInfo
    {
        public ScreenshotBitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowPlacement", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenshotGetWindowPlacement(
        IntPtr windowHandle,
        ref ScreenshotWindowPlacement placement);

    [DllImport("user32.dll", EntryPoint = "PrintWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenshotPrintWindow(IntPtr windowHandle, IntPtr targetDc, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern IntPtr ScreenshotGetDc(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int ScreenshotReleaseDc(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    private static extern IntPtr ScreenshotCreateCompatibleDc(IntPtr deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenshotDeleteDc(IntPtr deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    private static extern IntPtr ScreenshotCreateDibSection(
        IntPtr deviceContext,
        ref ScreenshotBitmapInfo bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll", EntryPoint = "SelectObject")]
    private static extern IntPtr ScreenshotSelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenshotDeleteObject(IntPtr graphicsObject);

    [DllImport("gdi32.dll", EntryPoint = "BitBlt", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenshotBitBlt(
        IntPtr destinationDc,
        int x,
        int y,
        int width,
        int height,
        IntPtr sourceDc,
        int sourceX,
        int sourceY,
        uint rasterOperation);
}
