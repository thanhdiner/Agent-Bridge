using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    public async Task<CommandResult<UiClickResult>> ClickAsync(
        string windowHandle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return ClickFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            return ClickFailure(commandId, ErrorCodes.InvalidRequest, "automationId or name is required.");
        if (occurrenceIndex is < 0 or > 1000)
            return ClickFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (!OperatingSystem.IsWindows())
            return ClickFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI interaction is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => ClickWindows(
                    handle,
                    automationId?.Trim(),
                    name?.Trim(),
                    controlType?.Trim(),
                    occurrenceIndex,
                    focusWindow,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ClickFailure(commandId, ErrorCodes.CommandCancelled, "The UI interaction request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation interaction failure for command {CommandId}", commandId);
            return ClickFailure(commandId, ErrorCodes.UiClickFailed, "Windows UI Automation could not activate the requested control.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI interaction failure for command {CommandId}", commandId);
            return ClickFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while activating the control.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiClickResult> ClickWindows(
        IntPtr handle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return ClickFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        try
        {
            automation = new CUIAutomation8();
            root = automation.ElementFromHandle(handle);
            if (root is null)
                return ClickFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");

            if (focusWindow)
                root.SetFocus();

            walker = automation.ControlViewWalker;
            var seen = 0;
            var visited = 0;
            match = FindClickTarget(
                root,
                walker,
                automationId,
                name,
                controlType,
                occurrenceIndex,
                ref seen,
                ref visited,
                cancellationToken);

            if (match is null)
                return ClickFailure(commandId, ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");

            var matchedName = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty));
            var matchedAutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty));
            var matchedControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0));
            var bounds = ReadBounds(match);
            var method = ActivateControl(match);
            if (method is null)
                return ClickFailure(commandId, ErrorCodes.UiClickFailed, "The matched control does not expose a supported activation pattern.");

            return new CommandResult<UiClickResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new UiClickResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    Name = matchedName,
                    AutomationId = matchedAutomationId,
                    ControlType = matchedControlType,
                    Bounds = bounds,
                    ClickMethod = method,
                    OccurrenceIndex = occurrenceIndex
                }
            };
        }
        finally
        {
            if (!ReferenceEquals(match, root))
                ReleaseComObject(match);
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IUIAutomationElement? FindClickTarget(
        IUIAutomationElement parent,
        IUIAutomationTreeWalker walker,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        ref int seen,
        ref int visited,
        CancellationToken cancellationToken)
    {
        if (MatchesClickSelector(parent, automationId, name, controlType))
        {
            if (seen == occurrenceIndex)
                return parent;
            seen++;
        }

        IUIAutomationElement? current = null;
        try
        {
            current = walker.GetFirstChildElement(parent);
        }
        catch (COMException)
        {
            return null;
        }

        while (current is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > 5000)
            {
                ReleaseComObject(current);
                return null;
            }

            IUIAutomationElement? next = null;
            var transfer = false;
            try
            {
                var result = FindClickTarget(
                    current,
                    walker,
                    automationId,
                    name,
                    controlType,
                    occurrenceIndex,
                    ref seen,
                    ref visited,
                    cancellationToken);
                if (result is not null)
                {
                    transfer = ReferenceEquals(result, current);
                    return result;
                }

                next = walker.GetNextSiblingElement(current);
            }
            catch (COMException)
            {
                next = null;
            }
            finally
            {
                if (!transfer)
                    ReleaseComObject(current);
            }

            current = next;
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static bool MatchesClickSelector(
        IUIAutomationElement element,
        string? automationId,
        string? name,
        string? controlType)
    {
        if (!string.IsNullOrEmpty(automationId)
            && !string.Equals(
                ReadOrDefault(() => element.CurrentAutomationId, string.Empty),
                automationId,
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(name)
            && !string.Equals(
                ReadOrDefault(() => element.CurrentName, string.Empty),
                name,
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(controlType)
            && !string.Equals(
                GetControlTypeName(ReadOrDefault(() => element.CurrentControlType, 0)),
                controlType,
                StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static string? ActivateControl(IUIAutomationElement element)
    {
        object? pattern = null;
        try
        {
            pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId);
            if (pattern is IUIAutomationInvokePattern invokePattern)
            {
                invokePattern.Invoke();
                return "invoke";
            }
        }
        catch (COMException)
        {
        }
        finally
        {
            ReleaseComObject(pattern);
        }

        try
        {
            pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_SelectionItemPatternId);
            if (pattern is IUIAutomationSelectionItemPattern selectionPattern)
            {
                selectionPattern.Select();
                return "selection-item";
            }
        }
        catch (COMException)
        {
        }
        finally
        {
            ReleaseComObject(pattern);
        }

        try
        {
            pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_TogglePatternId);
            if (pattern is IUIAutomationTogglePattern togglePattern)
            {
                togglePattern.Toggle();
                return "toggle";
            }
        }
        catch (COMException)
        {
        }
        finally
        {
            ReleaseComObject(pattern);
        }

        return null;
    }

    private static CommandResult<UiClickResult> ClickFailure(Guid commandId, string code, string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };
}
