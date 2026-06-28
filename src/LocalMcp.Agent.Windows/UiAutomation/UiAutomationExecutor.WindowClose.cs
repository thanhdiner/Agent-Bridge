using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    public async Task<CommandResult<WindowCloseResult>> CloseWindowAsync(
        string windowHandle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return CloseFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (!OperatingSystem.IsWindows())
            return CloseFailure(commandId, ErrorCodes.UiAutomationUnavailable, "Window close is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => CloseWindowWindows(handle, commandId, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return CloseFailure(commandId, ErrorCodes.CommandCancelled, "The window close request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation close failure for command {CommandId}", commandId);
            return CloseFailure(commandId, ErrorCodes.WindowCloseFailed, "Windows UI Automation could not close the requested window.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected window close failure for command {CommandId}", commandId);
            return CloseFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while closing the window.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<WindowCloseResult> CloseWindowWindows(
        IntPtr handle,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return CloseFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        cancellationToken.ThrowIfCancellationRequested();
        GetWindowThreadProcessId(handle, out var processId);
        IUIAutomation? automation = null;
        IUIAutomationElement? element = null;
        object? pattern = null;
        try
        {
            automation = new CUIAutomation8();
            element = automation.ElementFromHandle(handle);
            if (element is null)
                return CloseFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");

            pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_WindowPatternId);
            if (pattern is not IUIAutomationWindowPattern windowPattern)
                return CloseFailure(commandId, ErrorCodes.WindowCloseFailed, "The requested window does not expose WindowPattern.Close.");

            windowPattern.Close();
            var closed = false;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsWindow(handle))
                {
                    closed = true;
                    break;
                }
                Thread.Sleep(100);
            }

            return new CommandResult<WindowCloseResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new WindowCloseResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    ProcessId = processId > int.MaxValue ? 0 : (int)processId,
                    CloseRequested = true,
                    Closed = closed
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

    private static CommandResult<WindowCloseResult> CloseFailure(Guid commandId, string code, string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };
}
