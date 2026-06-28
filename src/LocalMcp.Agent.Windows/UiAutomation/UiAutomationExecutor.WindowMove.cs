using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    public async Task<CommandResult<WindowMoveResult>> MoveWindowAsync(
        string windowHandle,
        int x,
        int y,
        int width,
        int height,
        bool restoreIfNeeded,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return MoveError(commandId, ErrorCodes.InvalidRequest, "Invalid windowHandle.");
        if (width is < 1 or > 100000 || height is < 1 or > 100000)
            return MoveError(commandId, ErrorCodes.InvalidRequest, "Invalid width or height.");
        if (x is < -100000 or > 100000 || y is < -100000 or > 100000)
            return MoveError(commandId, ErrorCodes.InvalidRequest, "Invalid x or y.");
        if (!OperatingSystem.IsWindows())
            return MoveError(commandId, ErrorCodes.UiAutomationUnavailable, "Windows only.");

        try
        {
            return await Task.Run(
                () => MoveWindowWindows(handle, x, y, width, height, restoreIfNeeded, commandId, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return MoveError(commandId, ErrorCodes.CommandCancelled, "The request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation transform failure for {CommandId}", commandId);
            return MoveError(commandId, ErrorCodes.WindowMoveFailed, "The window could not be transformed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected transform failure for {CommandId}", commandId);
            return MoveError(commandId, ErrorCodes.InternalError, "Unexpected window transform failure.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<WindowMoveResult> MoveWindowWindows(
        IntPtr handle,
        int x,
        int y,
        int width,
        int height,
        bool restoreIfNeeded,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return MoveError(commandId, ErrorCodes.WindowNotFound, "Window not found.");

        cancellationToken.ThrowIfCancellationRequested();
        var wasMinimized = IsIconic(handle);
        var wasMaximized = IsZoomed(handle);
        IUIAutomation? automation = null;
        IUIAutomationElement? element = null;
        object? transformObject = null;
        object? windowObject = null;
        try
        {
            automation = new CUIAutomation8();
            element = automation.ElementFromHandle(handle);
            if (element is null)
                return MoveError(commandId, ErrorCodes.WindowNotFound, "Window not found.");

            var restored = false;
            if (restoreIfNeeded && (wasMinimized || wasMaximized))
            {
                windowObject = element.GetCurrentPattern(UIA_PatternIds.UIA_WindowPatternId);
                if (windowObject is IUIAutomationWindowPattern windowPattern)
                {
                    windowPattern.SetWindowVisualState(WindowVisualState.WindowVisualState_Normal);
                    restored = true;
                }
            }

            transformObject = element.GetCurrentPattern(UIA_PatternIds.UIA_TransformPatternId);
            if (transformObject is not IUIAutomationTransformPattern transform)
                return MoveError(commandId, ErrorCodes.WindowMoveFailed, "TransformPattern is unavailable.");
            if (transform.CurrentCanMove == 0 || transform.CurrentCanResize == 0)
                return MoveError(commandId, ErrorCodes.WindowMoveFailed, "Move or resize is not supported.");

            transform.Move(x, y);
            transform.Resize(width, height);
            Thread.Sleep(50);
            if (!GetWindowRect(handle, out var rectangle))
                return MoveError(commandId, ErrorCodes.WindowMoveFailed, "Final bounds could not be read.");

            return new CommandResult<WindowMoveResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new WindowMoveResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    WasMinimized = wasMinimized,
                    WasMaximized = wasMaximized,
                    Restored = restored,
                    Bounds = new UiBounds
                    {
                        X = rectangle.Left,
                        Y = rectangle.Top,
                        Width = rectangle.Right - rectangle.Left,
                        Height = rectangle.Bottom - rectangle.Top
                    }
                }
            };
        }
        finally
        {
            ReleaseComObject(windowObject);
            ReleaseComObject(transformObject);
            ReleaseComObject(element);
            ReleaseComObject(automation);
        }
    }

    private static CommandResult<WindowMoveResult> MoveError(Guid commandId, string code, string message) =>
        new() { CommandId = commandId, Success = false, Error = new CommandError(code, message) };
}
