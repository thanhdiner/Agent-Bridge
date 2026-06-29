using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const int MaxFileDialogPathCharacters = 32_767;
    private const int MaxFileDialogTraversalNodes = 4_096;

    public async Task<CommandResult<FileDialogSetPathResult>> FileDialogSetPathAsync(
        string windowHandle,
        string path,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        bool submit,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return FileDialogFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrWhiteSpace(path))
            return FileDialogFailure(commandId, ErrorCodes.InvalidRequest, "path is required.");
        if (path.Length > MaxFileDialogPathCharacters || path.Contains('\0'))
            return FileDialogFailure(commandId, ErrorCodes.InvalidRequest, $"path must be at most {MaxFileDialogPathCharacters} characters and contain no NUL characters.");
        if (!ValidateKeyboardSelector(automationId, name, controlType, occurrenceIndex, requireSelector: false, out var selectorError))
            return FileDialogFailure(commandId, ErrorCodes.InvalidRequest, selectorError);
        if (!OperatingSystem.IsWindows())
            return FileDialogFailure(commandId, ErrorCodes.UiAutomationUnavailable, "File dialog automation is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => FileDialogSetPathWindows(
                    handle,
                    path,
                    automationId?.Trim(),
                    name?.Trim(),
                    controlType?.Trim(),
                    occurrenceIndex,
                    focusWindow,
                    submit,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return FileDialogFailure(commandId, ErrorCodes.CommandCancelled, "The file dialog path request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation file dialog failure for command {CommandId}", commandId);
            return FileDialogFailure(commandId, ErrorCodes.FileDialogPathSetFailed, "Windows UI Automation could not update the file dialog.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected file dialog path failure for command {CommandId}", commandId);
            return FileDialogFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while updating the file dialog.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<FileDialogSetPathResult> FileDialogSetPathWindows(
        IntPtr handle,
        string path,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        bool submit,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        try
        {
            var hasExplicitSelector = !string.IsNullOrEmpty(automationId) || !string.IsNullOrEmpty(name);
            var prepareError = PrepareKeyboardTarget(
                handle,
                hasExplicitSelector ? automationId : null,
                hasExplicitSelector ? name : null,
                hasExplicitSelector ? controlType : null,
                occurrenceIndex,
                focusWindow,
                cancellationToken,
                out automation,
                out walker,
                out root,
                out match);
            if (prepareError is not null)
                return FileDialogFailure(commandId, prepareError.Code, prepareError.Message);
            if (automation is null || root is null)
                return FileDialogFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the file dialog window.");

            if (!hasExplicitSelector)
            {
                walker ??= automation.ControlViewWalker;
                match = FindBestFileDialogEdit(root, walker, cancellationToken);
            }

            if (match is null)
            {
                return FileDialogFailure(
                    commandId,
                    ErrorCodes.FileDialogFieldNotFound,
                    "No writable file-name edit control was found. Supply automationId or name for this dialog implementation.");
            }
            if (ReadOrDefault(() => match.CurrentIsEnabled == 0, true))
                return FileDialogFailure(commandId, ErrorCodes.FileDialogPathSetFailed, "The matched file-name control is disabled.");
            if (ReadOrDefault(() => match.CurrentIsPassword != 0, false))
                return FileDialogFailure(commandId, ErrorCodes.FileDialogPathSetFailed, "Refusing to write a file path into a password control.");

            match.SetFocus();
            var writeMethod = TryWriteValue(match, path, out var readOnly);
            if (writeMethod is null)
            {
                return FileDialogFailure(
                    commandId,
                    readOnly ? ErrorCodes.UiValueReadOnly : ErrorCodes.FileDialogPathSetFailed,
                    readOnly
                        ? "The matched file-name control is read-only."
                        : "The matched file-name control does not expose a writable value pattern.");
            }

            if (!WaitForValue(match, path, cancellationToken))
            {
                return FileDialogFailure(
                    commandId,
                    ErrorCodes.FileDialogPathVerificationFailed,
                    "The file-name control accepted the write request but did not retain the requested path.");
            }

            var submitted = false;
            if (submit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GetForegroundWindow() != handle)
                {
                    return FileDialogFailure(
                        commandId,
                        ErrorCodes.UiForegroundMismatch,
                        "The foreground window changed before the file dialog could be submitted.");
                }

                var enterInputs = new[]
                {
                    BuildVirtualKeyInput(VkReturn, keyUp: false),
                    BuildVirtualKeyInput(VkReturn, keyUp: true)
                };
                var sent = SendInput((uint)enterInputs.Length, enterInputs, Marshal.SizeOf<NativeInput>());
                if (sent != (uint)enterInputs.Length)
                {
                    return FileDialogFailure(
                        commandId,
                        ErrorCodes.FileDialogSubmitFailed,
                        $"SendInput accepted {sent} of {enterInputs.Length} Enter key events.");
                }
                submitted = true;
            }

            return new CommandResult<FileDialogSetPathResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new FileDialogSetPathResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    Name = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty)),
                    AutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty)),
                    ControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0)),
                    Bounds = ReadBounds(match),
                    OccurrenceIndex = hasExplicitSelector ? occurrenceIndex : 0,
                    PathLength = path.Length,
                    Verified = true,
                    Submitted = submitted
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
    private static IUIAutomationElement? FindBestFileDialogEdit(
        IUIAutomationElement root,
        IUIAutomationTreeWalker walker,
        CancellationToken cancellationToken)
    {
        var rootBounds = ReadBounds(root);
        var visited = 0;
        var bestScore = int.MinValue;
        IUIAutomationElement? best = null;

        SearchFileDialogChildren(
            root,
            walker,
            rootBounds,
            cancellationToken,
            ref visited,
            ref bestScore,
            ref best);
        return best;
    }

    [SupportedOSPlatform("windows")]
    private static void SearchFileDialogChildren(
        IUIAutomationElement parent,
        IUIAutomationTreeWalker walker,
        UiBounds rootBounds,
        CancellationToken cancellationToken,
        ref int visited,
        ref int bestScore,
        ref IUIAutomationElement? best)
    {
        if (visited >= MaxFileDialogTraversalNodes)
            return;

        IUIAutomationElement? current = null;
        try
        {
            current = walker.GetFirstChildElement(parent);
        }
        catch (COMException)
        {
            return;
        }

        while (current is not null && visited < MaxFileDialogTraversalNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            visited++;
            IUIAutomationElement? next = null;
            var keepCurrent = false;
            try
            {
                next = walker.GetNextSiblingElement(current);
                var score = ScoreFileDialogEdit(current, rootBounds);
                SearchFileDialogChildren(
                    current,
                    walker,
                    rootBounds,
                    cancellationToken,
                    ref visited,
                    ref bestScore,
                    ref best);

                if (score > bestScore)
                {
                    ReleaseComObject(best);
                    best = current;
                    bestScore = score;
                    keepCurrent = true;
                }
            }
            catch (COMException)
            {
            }
            finally
            {
                if (!keepCurrent)
                    ReleaseComObject(current);
            }

            current = next;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int ScoreFileDialogEdit(IUIAutomationElement element, UiBounds rootBounds)
    {
        if (ReadOrDefault(() => element.CurrentControlType, 0) != UIA_ControlTypeIds.UIA_EditControlTypeId)
            return int.MinValue;
        if (ReadOrDefault(() => element.CurrentIsEnabled == 0, true)
            || ReadOrDefault(() => element.CurrentIsOffscreen != 0, true)
            || ReadOrDefault(() => element.CurrentIsPassword != 0, false))
        {
            return int.MinValue;
        }
        if (!HasWritableValuePattern(element))
            return int.MinValue;

        var automationId = ReadOrDefault(() => element.CurrentAutomationId, string.Empty);
        var name = ReadOrDefault(() => element.CurrentName, string.Empty);
        var bounds = ReadBounds(element);
        var score = 100;

        if (string.Equals(automationId, "1148", StringComparison.OrdinalIgnoreCase))
            score += 1_000;
        if (automationId.Contains("filename", StringComparison.OrdinalIgnoreCase)
            || automationId.Contains("file_name", StringComparison.OrdinalIgnoreCase))
        {
            score += 800;
        }
        if (name.Contains("file name", StringComparison.OrdinalIgnoreCase)
            || name.Contains("filename", StringComparison.OrdinalIgnoreCase))
        {
            score += 700;
        }
        if (rootBounds.Height > 0 && bounds.Y >= rootBounds.Y + (rootBounds.Height / 2))
            score += 50;
        if (bounds.Width >= 150)
            score += 25;

        return score;
    }

    [SupportedOSPlatform("windows")]
    private static bool HasWritableValuePattern(IUIAutomationElement element)
    {
        object? pattern = null;
        try
        {
            pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId);
            return pattern is IUIAutomationValuePattern valuePattern
                && valuePattern.CurrentIsReadOnly == 0;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(pattern);
        }
    }

    private static CommandResult<FileDialogSetPathResult> FileDialogFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };
}
