using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    public async Task<CommandResult<UiTextReadResult>> TextReadAsync(
        string windowHandle,
        string scope,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        int startLine,
        int lineCount,
        int maxCharacters,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return TextReadFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (!UiTextReadScopes.TryNormalize(scope, out var normalizedScope))
            return TextReadFailure(commandId, ErrorCodes.InvalidRequest, "scope must be one of: document, visible, selection.");
        if (string.IsNullOrWhiteSpace(automationId)
            && string.IsNullOrWhiteSpace(name)
            && string.IsNullOrWhiteSpace(controlType))
            return TextReadFailure(commandId, ErrorCodes.InvalidRequest, "automationId, name, or controlType is required.");
        if (automationId?.Length > 1024 || name?.Length > 1024 || controlType?.Length > 128)
            return TextReadFailure(commandId, ErrorCodes.InvalidRequest, "Selector values exceed their maximum lengths.");
        if (occurrenceIndex is < 0 or > 1000)
            return TextReadFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (startLine is < 0 or > 1_000_000)
            return TextReadFailure(commandId, ErrorCodes.InvalidRequest, "startLine must be between 0 and 1000000.");
        if (lineCount is < 1 or > 10_000)
            return TextReadFailure(commandId, ErrorCodes.InvalidRequest, "lineCount must be between 1 and 10000.");
        if (maxCharacters is < 1 or > 65_536)
            return TextReadFailure(commandId, ErrorCodes.UiTextLimitExceeded, "maxCharacters must be between 1 and 65536.");
        if (!OperatingSystem.IsWindows())
            return TextReadFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI text reading is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => TextReadWindows(
                    handle,
                    normalizedScope,
                    automationId?.Trim(),
                    name?.Trim(),
                    controlType?.Trim(),
                    occurrenceIndex,
                    startLine,
                    lineCount,
                    maxCharacters,
                    focusWindow,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return TextReadFailure(commandId, ErrorCodes.CommandCancelled, "The UI text read request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation text read failure for command {CommandId}", commandId);
            return TextReadFailure(commandId, ErrorCodes.UiTextReadFailed, "Windows UI Automation could not read the requested text.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI text read failure for command {CommandId}", commandId);
            return TextReadFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while reading text.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiTextReadResult> TextReadWindows(
        IntPtr handle,
        string scope,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        int startLine,
        int lineCount,
        int maxCharacters,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return TextReadFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        object? textPatternObject = null;
        object? textPattern2Object = null;
        try
        {
            automation = CreateAutomationClient();
            root = automation.ElementFromHandle(handle);
            if (root is null)
                return TextReadFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");
            if (focusWindow)
                root.SetFocus();

            walker = automation.ControlViewWalker;
            var seen = 0;
            var visited = 0;
            match = FindTarget(root, walker, automationId, name, controlType, occurrenceIndex, ref seen, ref visited, cancellationToken);
            if (match is null)
                return TextReadFailure(commandId, ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");
            if (focusWindow)
                match.SetFocus();

            var isPassword = ReadOrDefault(() => match.CurrentIsPassword != 0, false);
            textPatternObject = TryGetTextPattern(match, UIA_PatternIds.UIA_TextPatternId);
            textPattern2Object = TryGetTextPattern(match, UIA_PatternIds.UIA_TextPattern2Id);
            var textPattern = textPatternObject as IUIAutomationTextPattern;
            var textPattern2 = textPattern2Object as IUIAutomationTextPattern2;
            var patternsUsed = new List<string>();
            if (textPattern is not null)
                patternsUsed.Add("text");
            if (textPattern2 is not null)
                patternsUsed.Add("text2");

            if (isPassword)
            {
                return BuildTextReadSuccess(
                    handle,
                    root,
                    match,
                    occurrenceIndex,
                    scope,
                    startLine,
                    lineCount,
                    new TextReadPayload(null, 0, true, 0, 0, false),
                    selectionCount: 0,
                    isReadOnly: null,
                    isPassword: true,
                    caretPosition: null,
                    caretPositionExact: false,
                    patternsUsed,
                    commandId);
            }

            if (textPattern is not null)
            {
                if (!TryReadTextPatternScope(
                        textPattern,
                        scope,
                        startLine,
                        lineCount,
                        maxCharacters,
                        cancellationToken,
                        out var payload,
                        out var errorCode,
                        out var errorMessage))
                {
                    return TextReadFailure(commandId, errorCode!, errorMessage!);
                }

                var selectionCount = TryReadSelectionCount(textPattern);
                var isReadOnly = TryReadTextIsReadOnly(textPattern);
                var caretPositionExact = false;
                var caretPosition = textPattern2 is null
                    ? null
                    : TryReadCaretPosition(textPattern2, textPattern, maxCharacters, out caretPositionExact);

                return BuildTextReadSuccess(
                    handle,
                    root,
                    match,
                    occurrenceIndex,
                    scope,
                    startLine,
                    lineCount,
                    payload,
                    selectionCount,
                    isReadOnly,
                    isPassword: false,
                    caretPosition,
                    caretPositionExact,
                    patternsUsed,
                    commandId);
            }

            if (scope != UiTextReadScopes.Document)
                return TextReadFailure(commandId, ErrorCodes.UiTextNotSupported, "Visible and selection scopes require TextPattern support.");

            if (!TryReadFallbackText(match, out var fallbackText, out var fallbackPattern, out var fallbackReadOnly))
                return TextReadFailure(commandId, ErrorCodes.UiTextNotSupported, "The matched control does not expose TextPattern, ValuePattern, or a readable Legacy Accessible value.");

            patternsUsed.Add(fallbackPattern!);
            if (!TrySlicePlainText(
                    fallbackText!,
                    startLine,
                    lineCount,
                    maxCharacters,
                    out var fallbackPayload))
            {
                return TextReadFailure(commandId, ErrorCodes.UiTextRangeNotAvailable, "startLine is outside the available text range.");
            }

            return BuildTextReadSuccess(
                handle,
                root,
                match,
                occurrenceIndex,
                scope,
                startLine,
                lineCount,
                fallbackPayload,
                selectionCount: 0,
                fallbackReadOnly,
                isPassword: false,
                caretPosition: null,
                caretPositionExact: false,
                patternsUsed,
                commandId);
        }
        finally
        {
            ReleaseComObject(textPattern2Object);
            ReleaseComObject(textPatternObject);
            if (!ReferenceEquals(match, root))
                ReleaseComObject(match);
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiTextReadResult> BuildTextReadSuccess(
        IntPtr handle,
        IUIAutomationElement root,
        IUIAutomationElement match,
        int occurrenceIndex,
        string scope,
        int startLine,
        int requestedLineCount,
        TextReadPayload payload,
        int selectionCount,
        bool? isReadOnly,
        bool isPassword,
        int? caretPosition,
        bool caretPositionExact,
        IReadOnlyList<string> patternsUsed,
        Guid commandId) => new()
    {
        CommandId = commandId,
        Success = true,
        Data = new UiTextReadResult
        {
            WindowHandle = FormatWindowHandle(handle),
            ProcessId = ReadOrDefault(() => root.CurrentProcessId, 0),
            Name = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty)),
            AutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty)),
            ControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0)),
            Bounds = ReadBounds(match),
            OccurrenceIndex = occurrenceIndex,
            Scope = scope,
            Text = payload.Text,
            CharacterCount = payload.CharacterCount,
            CharacterCountExact = payload.CharacterCountExact,
            ReturnedCharacters = payload.ReturnedCharacters,
            StartLine = startLine,
            RequestedLineCount = requestedLineCount,
            ReturnedLineCount = payload.ReturnedLineCount,
            SelectionCount = selectionCount,
            IsReadOnly = isReadOnly,
            IsPassword = isPassword,
            Redacted = isPassword,
            CaretPosition = caretPosition,
            CaretPositionExact = caretPositionExact,
            PatternsUsed = patternsUsed,
            Truncated = payload.Truncated
        }
    };

    [SupportedOSPlatform("windows")]
    private static object? TryGetTextPattern(IUIAutomationElement element, int patternId)
    {
        try
        {
            return element.GetCurrentPattern(patternId);
        }
        catch (COMException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadFallbackText(
        IUIAutomationElement element,
        out string? text,
        out string? patternName,
        out bool? isReadOnly)
    {
        object? patternObject = null;
        try
        {
            patternObject = element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId);
            if (patternObject is IUIAutomationValuePattern valuePattern)
            {
                text = valuePattern.CurrentValue ?? string.Empty;
                patternName = "value";
                isReadOnly = valuePattern.CurrentIsReadOnly != 0;
                return true;
            }
        }
        catch (COMException)
        {
        }
        finally
        {
            ReleaseComObject(patternObject);
        }

        patternObject = null;
        try
        {
            patternObject = element.GetCurrentPattern(UIA_PatternIds.UIA_LegacyIAccessiblePatternId);
            if (patternObject is IUIAutomationLegacyIAccessiblePattern legacyPattern)
            {
                text = legacyPattern.CurrentValue ?? string.Empty;
                patternName = "legacy-accessible";
                isReadOnly = null;
                return true;
            }
        }
        catch (COMException)
        {
        }
        finally
        {
            ReleaseComObject(patternObject);
        }

        text = null;
        patternName = null;
        isReadOnly = null;
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadTextPatternScope(
        IUIAutomationTextPattern textPattern,
        string scope,
        int startLine,
        int lineCount,
        int maxCharacters,
        CancellationToken cancellationToken,
        out TextReadPayload payload,
        out string? errorCode,
        out string? errorMessage)
    {
        IUIAutomationTextRange? documentRange = null;
        IUIAutomationTextRangeArray? rangeArray = null;
        var ranges = new List<IUIAutomationTextRange>();
        try
        {
            if (scope == UiTextReadScopes.Document)
            {
                documentRange = textPattern.DocumentRange;
                if (documentRange is null)
                {
                    payload = default;
                    errorCode = ErrorCodes.UiTextRangeNotAvailable;
                    errorMessage = "The document text range is not available.";
                    return false;
                }

                ranges.Add(documentRange);
            }
            else
            {
                rangeArray = scope == UiTextReadScopes.Visible
                    ? textPattern.GetVisibleRanges()
                    : textPattern.GetSelection();
                if (rangeArray is null || rangeArray.Length <= 0)
                {
                    payload = default;
                    errorCode = ErrorCodes.UiTextRangeNotAvailable;
                    errorMessage = scope == UiTextReadScopes.Visible
                        ? "No visible text range is currently available."
                        : "No selected text range is currently available.";
                    return false;
                }

                for (var index = 0; index < rangeArray.Length; index++)
                {
                    var range = rangeArray.GetElement(index);
                    if (range is not null)
                        ranges.Add(range);
                }
            }

            if (ranges.Count == 0)
            {
                payload = default;
                errorCode = ErrorCodes.UiTextRangeNotAvailable;
                errorMessage = "The requested text range is not available.";
                return false;
            }

            if (!TryReadTextRanges(
                    ranges,
                    startLine,
                    lineCount,
                    maxCharacters,
                    cancellationToken,
                    out payload))
            {
                errorCode = ErrorCodes.UiTextRangeNotAvailable;
                errorMessage = "startLine is outside the available text range.";
                return false;
            }

            errorCode = null;
            errorMessage = null;
            return true;
        }
        catch (COMException)
        {
            payload = default;
            errorCode = ErrorCodes.UiTextReadFailed;
            errorMessage = "The requested UI Automation text range could not be read.";
            return false;
        }
        finally
        {
            foreach (var range in ranges)
            {
                if (!ReferenceEquals(range, documentRange))
                    ReleaseComObject(range);
            }
            ReleaseComObject(documentRange);
            ReleaseComObject(rangeArray);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadTextRanges(
        IReadOnlyList<IUIAutomationTextRange> ranges,
        int startLine,
        int lineCount,
        int maxCharacters,
        CancellationToken cancellationToken,
        out TextReadPayload payload)
    {
        var builder = new StringBuilder(Math.Min(maxCharacters + 1, 4096));
        var linesToSkip = startLine;
        var linesRemaining = lineCount;
        var characterLimitHit = false;
        var windowTruncated = startLine > 0;
        var reachedReadableRange = startLine == 0;
        IUIAutomationTextRange? previousSourceEnd = null;

        try
        {
            for (var index = 0; index < ranges.Count && linesRemaining > 0 && !characterLimitHit; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = ranges[index];
                IUIAutomationTextRange? slice = null;
                IUIAutomationTextRange? endMarker = null;
                var updatePreviousEnd = false;
                try
                {
                    if (previousSourceEnd is not null
                        && source.CompareEndpoints(
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
                            previousSourceEnd,
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) <= 0)
                    {
                        continue;
                    }

                    updatePreviousEnd = true;
                    slice = source.Clone();
                    if (slice is null)
                        continue;

                    if (previousSourceEnd is not null
                        && slice.CompareEndpoints(
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                            previousSourceEnd,
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) < 0)
                    {
                        slice.MoveEndpointByRange(
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                            previousSourceEnd,
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
                    }

                    if (linesToSkip > 0)
                    {
                        var moved = Math.Max(
                            0,
                            slice.MoveEndpointByUnit(
                                TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                                TextUnit.TextUnit_Line,
                                linesToSkip));
                        linesToSkip -= moved;
                        if (linesToSkip > 0)
                            continue;
                        reachedReadableRange = true;
                    }

                    if (slice.CompareEndpoints(
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                            slice,
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) >= 0)
                        continue;

                    endMarker = slice.Clone();
                    if (endMarker is null)
                        continue;
                    endMarker.MoveEndpointByRange(
                        TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
                        endMarker,
                        TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
                    endMarker.MoveEndpointByUnit(
                        TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
                        TextUnit.TextUnit_Line,
                        linesRemaining);
                    if (endMarker.CompareEndpoints(
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
                            source,
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) > 0)
                    {
                        endMarker.MoveEndpointByRange(
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
                            source,
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
                    }
                    slice.MoveEndpointByRange(
                        TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
                        endMarker,
                        TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);

                    var hasMoreInRange = slice.CompareEndpoints(
                        TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
                        source,
                        TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) < 0;
                    var remainingCapacity = maxCharacters + 1 - builder.Length;
                    if (remainingCapacity <= 0)
                    {
                        characterLimitHit = true;
                        break;
                    }

                    var text = slice.GetText(remainingCapacity + 1) ?? string.Empty;
                    var shouldSeparate = builder.Length > 0
                        && text.Length > 0
                        && previousSourceEnd is not null
                        && source.CompareEndpoints(
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                            previousSourceEnd,
                            TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) > 0;
                    if (shouldSeparate)
                    {
                        if (builder.Length >= maxCharacters + 1)
                        {
                            characterLimitHit = true;
                            break;
                        }
                        builder.Append('\n');
                    }

                    remainingCapacity = maxCharacters + 1 - builder.Length;
                    if (text.Length > remainingCapacity)
                    {
                        builder.Append(text.AsSpan(0, remainingCapacity));
                        characterLimitHit = true;
                    }
                    else
                    {
                        builder.Append(text);
                    }

                    var linesRead = CountTextLines(text);
                    linesRemaining = Math.Max(0, linesRemaining - linesRead);
                    if ((hasMoreInRange || index < ranges.Count - 1) && linesRemaining == 0)
                        windowTruncated = true;
                }
                finally
                {
                    ReleaseComObject(endMarker);
                    ReleaseComObject(slice);
                    if (updatePreviousEnd)
                    {
                        IUIAutomationTextRange? nextEnd = null;
                        try
                        {
                            nextEnd = source.Clone();
                            nextEnd?.MoveEndpointByRange(
                                TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                                nextEnd,
                                TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
                            ReleaseComObject(previousSourceEnd);
                            previousSourceEnd = nextEnd;
                            nextEnd = null;
                        }
                        finally
                        {
                            ReleaseComObject(nextEnd);
                        }
                    }
                }
            }
        }
        finally
        {
            ReleaseComObject(previousSourceEnd);
        }

        if (!reachedReadableRange || linesToSkip > 0)
        {
            payload = default;
            return false;
        }

        var observedText = builder.ToString();
        observedText = TrimTextToLineCount(observedText, lineCount, out var lineLimitHit);
        var observedCharacters = observedText.Length;
        if (observedText.Length > maxCharacters)
        {
            observedText = observedText[..maxCharacters];
            characterLimitHit = true;
        }

        payload = new TextReadPayload(
            observedText,
            observedCharacters,
            !characterLimitHit,
            observedText.Length,
            CountTextLines(observedText),
            windowTruncated || lineLimitHit || characterLimitHit);
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static int TryReadSelectionCount(IUIAutomationTextPattern textPattern)
    {
        IUIAutomationTextRangeArray? selection = null;
        try
        {
            selection = textPattern.GetSelection();
            return Math.Max(0, selection?.Length ?? 0);
        }
        catch (COMException)
        {
            return 0;
        }
        finally
        {
            ReleaseComObject(selection);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool? TryReadTextIsReadOnly(IUIAutomationTextPattern textPattern)
    {
        IUIAutomationTextRange? documentRange = null;
        try
        {
            documentRange = textPattern.DocumentRange;
            if (documentRange is null)
                return null;

            var value = documentRange.GetAttributeValue(UIA_TextAttributeIds.UIA_IsReadOnlyAttributeId);
            return value switch
            {
                bool booleanValue => booleanValue,
                int integerValue => integerValue != 0,
                _ => null
            };
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(documentRange);
        }
    }

    [SupportedOSPlatform("windows")]
    private static int? TryReadCaretPosition(
        IUIAutomationTextPattern2 textPattern2,
        IUIAutomationTextPattern textPattern,
        int maxCharacters,
        out bool exact)
    {
        exact = false;
        IUIAutomationTextRange? caretRange = null;
        IUIAutomationTextRange? documentRange = null;
        IUIAutomationTextRange? prefixRange = null;
        try
        {
            caretRange = textPattern2.GetCaretRange(out var isActive);
            if (caretRange is null || isActive == 0)
                return null;

            documentRange = textPattern.DocumentRange;
            if (documentRange is null)
                return null;

            prefixRange = documentRange.Clone();
            if (prefixRange is null)
                return null;
            prefixRange.MoveEndpointByRange(
                TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
                caretRange,
                TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);

            var prefix = prefixRange.GetText(maxCharacters + 1) ?? string.Empty;
            if (prefix.Length > maxCharacters)
                return null;

            exact = true;
            return prefix.Length;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(prefixRange);
            ReleaseComObject(documentRange);
            ReleaseComObject(caretRange);
        }
    }

    private static bool TrySlicePlainText(
        string text,
        int startLine,
        int lineCount,
        int maxCharacters,
        out TextReadPayload payload)
    {
        var lineStarts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                    index++;
                if (index + 1 < text.Length)
                    lineStarts.Add(index + 1);
            }
            else if (text[index] == '\n' && index + 1 < text.Length)
            {
                lineStarts.Add(index + 1);
            }
        }

        var totalLines = text.Length == 0 ? 0 : lineStarts.Count;
        if (text.Length == 0)
        {
            payload = startLine == 0
                ? new TextReadPayload(string.Empty, 0, true, 0, 0, false)
                : default;
            return startLine == 0;
        }
        if (startLine >= totalLines)
        {
            payload = default;
            return false;
        }

        var startIndex = lineStarts[startLine];
        var endLine = Math.Min(totalLines, startLine + lineCount);
        var endIndex = endLine < totalLines ? lineStarts[endLine] : text.Length;
        var selectedLength = Math.Max(0, endIndex - startIndex);
        var returnedLength = Math.Min(selectedLength, maxCharacters);
        var selected = text.Substring(startIndex, returnedLength);
        var truncated = startLine > 0 || endIndex < text.Length || selectedLength > maxCharacters;

        payload = new TextReadPayload(
            selected,
            selectedLength,
            true,
            selected.Length,
            CountTextLines(selected),
            truncated);
        return true;
    }

    private static string TrimTextToLineCount(string text, int maxLines, out bool trimmed)
    {
        trimmed = false;
        if (text.Length == 0 || maxLines <= 0)
            return text;

        var completedLines = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                completedLines++;
                if (completedLines >= maxLines && index + 1 < text.Length)
                {
                    trimmed = true;
                    return text[..index];
                }
                if (index + 1 < text.Length && text[index + 1] == '\n')
                    index++;
            }
            else if (text[index] == '\n')
            {
                completedLines++;
                if (completedLines >= maxLines && index + 1 < text.Length)
                {
                    trimmed = true;
                    return text[..index];
                }
            }
        }

        return text;
    }

    internal static int CountTextLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var count = 0;
        var hasCharactersAfterLastBreak = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                count++;
                hasCharactersAfterLastBreak = false;
                if (index + 1 < text.Length && text[index + 1] == '\n')
                    index++;
            }
            else if (text[index] == '\n')
            {
                count++;
                hasCharactersAfterLastBreak = false;
            }
            else
            {
                hasCharactersAfterLastBreak = true;
            }
        }

        return count + (hasCharactersAfterLastBreak ? 1 : 0);
    }

    private readonly record struct TextReadPayload(
        string? Text,
        int CharacterCount,
        bool CharacterCountExact,
        int ReturnedCharacters,
        int ReturnedLineCount,
        bool Truncated);

    private static CommandResult<UiTextReadResult> TextReadFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };
}
