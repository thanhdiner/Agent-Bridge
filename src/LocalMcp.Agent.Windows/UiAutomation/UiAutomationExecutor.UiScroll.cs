using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
namespace LocalMcp.Agent.Windows.UiAutomation;
public sealed partial class UiAutomationExecutor
{
    private const double NoScrollPercent = -1.0;
    private const double ScrollPercentEpsilon = 0.01;
    private const int ScrollObservationAttempts = 10;
    private const int ScrollObservationDelayMilliseconds = 50;
    private const int MaxScrollFallbackVisitedNodes = 5_000;
    private const int MaxScrollFallbackDepth = 30;
    private const int MaxScrollFallbackCandidates = MaxScrollFallbackVisitedNodes;
    private const int MaxScrollItemFallbackAttempts = 24;
    private const int MaxScrollVerificationMarkers = 64;
    private const int ScrollBoundsMovementTolerance = 1;
    private const int StrongScrollBoundsMovement = 12;
    public async Task<CommandResult<UiScrollResult>> ScrollAsync(
        string windowHandle,
        string direction,
        string amount,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return ScrollFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrWhiteSpace(automationId)
            && string.IsNullOrWhiteSpace(name)
            && string.IsNullOrWhiteSpace(controlType))
            return ScrollFailure(commandId, ErrorCodes.InvalidRequest, "automationId, name, or controlType is required.");
        if (automationId?.Length > 1024 || name?.Length > 1024 || controlType?.Length > 128)
            return ScrollFailure(commandId, ErrorCodes.InvalidRequest, "Selector values exceed their maximum lengths.");
        if (occurrenceIndex is < 0 or > 1000)
            return ScrollFailure(commandId, ErrorCodes.InvalidRequest, "occurrenceIndex must be between 0 and 1000.");
        if (!UiScrollDirections.TryNormalize(direction, out var normalizedDirection))
            return ScrollFailure(commandId, ErrorCodes.InvalidRequest, "direction must be one of: up, down, left, right.");
        if (!UiScrollAmounts.TryNormalize(amount, out var normalizedAmount))
            return ScrollFailure(commandId, ErrorCodes.InvalidRequest, "amount must be one of: small, page, end.");
        if (!OperatingSystem.IsWindows())
            return ScrollFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI scrolling is only available on Windows agents.");
        try
        {
            return await Task.Run(
                () => ScrollWindows(
                    handle,
                    normalizedDirection,
                    normalizedAmount,
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
            return ScrollFailure(commandId, ErrorCodes.CommandCancelled, "The UI scroll request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation scroll failure for command {CommandId}", commandId);
            return ScrollFailure(commandId, ErrorCodes.UiScrollFailed, "Windows UI Automation could not scroll the requested control.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI scroll failure for command {CommandId}", commandId);
            return ScrollFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while scrolling the control.");
        }
    }
    [SupportedOSPlatform("windows")]
    private static CommandResult<UiScrollResult> ScrollWindows(
        IntPtr handle,
        string direction,
        string amount,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return ScrollFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");
        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        object? rawPattern = null;
        try
        {
            automation = CreateAutomationClient();
            root = automation.ElementFromHandle(handle);
            if (root is null)
                return ScrollFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");
            var focusError = PrepareScrollWindow(handle, root, focusWindow, cancellationToken);
            if (focusError is not null)
                return ScrollFailure(commandId, focusError.Code, focusError.Message);
            walker = automation.ControlViewWalker;
            var seen = 0;
            var visited = 0;
            match = FindTarget(
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
                return ScrollFailure(commandId, ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");
            if (ReadOrDefault(() => match.CurrentIsEnabled == 0, true))
                return ScrollFailure(commandId, ErrorCodes.UiScrollFailed, "The matched control is disabled.");
            if (GetForegroundWindow() != handle)
                return ScrollFailure(commandId, ErrorCodes.UiForegroundMismatch, "The foreground window changed before the scroll action could run.");
            var matchedName = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty));
            var matchedAutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty));
            var matchedControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0));
            var matchedBounds = ReadBounds(match);
            var vertical = UiScrollDirections.IsVertical(direction);

            rawPattern = match.GetCurrentPattern(UIA_PatternIds.UIA_ScrollPatternId);
            var scrollPattern = rawPattern as IUIAutomationScrollPattern;
            var before = scrollPattern is null
                ? new ScrollSnapshot(false, false, null, null, 100.0, 100.0)
                : ReadScrollSnapshot(scrollPattern);
            var patternSupportsDirection = scrollPattern is not null
                && (vertical ? before.VerticalScrollable : before.HorizontalScrollable);

            if (patternSupportsDirection)
            {
                var method = ApplyScroll(scrollPattern!, direction, amount);
                var after = ObserveScrollResult(scrollPattern!, before, direction, amount, cancellationToken);
                return ScrollSuccess(
                    handle,
                    commandId,
                    matchedName,
                    matchedAutomationId,
                    matchedControlType,
                    matchedBounds,
                    direction,
                    amount,
                    method,
                    before,
                    after,
                    occurrenceIndex);
            }

            if (!vertical)
                return ScrollFailure(commandId, ErrorCodes.UiScrollNotSupported, "The matched control is not horizontally scrollable.");

            var scrollItemCandidates = FindScrollItemFallbackCandidates(
                match,
                walker,
                matchedBounds,
                direction,
                amount,
                cancellationToken);
            var fallbackVerificationBefore = CaptureScrollViewportSnapshot(
                match,
                walker,
                matchedBounds,
                cancellationToken);
            foreach (var candidate in scrollItemCandidates)
            {
                IUIAutomationElement? candidateElement = null;
                object? candidatePatternObject = null;
                try
                {
                    var currentOrdinal = 0;
                    var candidateVisited = 0;
                    candidateElement = FindScrollItemByOrdinal(
                        match,
                        walker,
                        candidate.Ordinal,
                        depth: 0,
                        ref currentOrdinal,
                        ref candidateVisited,
                        cancellationToken);
                    if (candidateElement is null)
                        continue;

                    var liveBounds = ReadBounds(candidateElement);
                    if (!GetScrollItemCandidateScore(liveBounds, matchedBounds, direction, amount).HasValue)
                        continue;

                    candidatePatternObject = candidateElement.GetCurrentPattern(UIA_PatternIds.UIA_ScrollItemPatternId);
                    if (candidatePatternObject is not IUIAutomationScrollItemPattern scrollItemPattern)
                        continue;

                    scrollItemPattern.ScrollIntoView();
                    if (!ObserveViewportMovement(
                            match,
                            walker,
                            matchedBounds,
                            fallbackVerificationBefore,
                            direction,
                            cancellationToken))
                    {
                        continue;
                    }

                    var fallbackAfter = scrollPattern is null
                        ? before with { VerticalScrollable = true }
                        : ReadScrollSnapshot(scrollPattern) with { VerticalScrollable = true };
                    return ScrollSuccess(
                        handle,
                        commandId,
                        matchedName,
                        matchedAutomationId,
                        matchedControlType,
                        matchedBounds,
                        direction,
                        amount,
                        "scroll-item-pattern",
                        before,
                        fallbackAfter,
                        occurrenceIndex,
                        changedOverride: true);
                }
                catch (COMException)
                {
                    // Chromium can expose stale or hidden accessibility nodes. Try the next ranked item.
                }
                finally
                {
                    ReleaseComObject(candidatePatternObject);
                    ReleaseComObject(candidateElement);
                }
            }

            if (!CanUseKeyboardScrollFallback(matchedControlType))
                return ScrollFailure(commandId, ErrorCodes.UiScrollNotSupported, "The matched control is not vertically scrollable and does not support a verified fallback.");

            var keyboardVerificationBefore = CaptureScrollViewportSnapshot(
                match,
                walker,
                matchedBounds,
                cancellationToken);
            if (keyboardVerificationBefore.Markers.Count == 0)
            {
                return ScrollFailure(
                    commandId,
                    ErrorCodes.UiScrollNotSupported,
                    "No stable UI Automation markers were available to verify keyboard scrolling.");
            }

            var keyboardError = ApplyKeyboardScroll(match, direction, amount);
            if (keyboardError is not null)
                return ScrollFailure(commandId, keyboardError.Code, keyboardError.Message);

            var keyboardChanged = ObserveViewportMovement(
                match,
                walker,
                matchedBounds,
                keyboardVerificationBefore,
                direction,
                cancellationToken);
            var keyboardAfter = scrollPattern is null
                ? before with { VerticalScrollable = true }
                : ReadScrollSnapshot(scrollPattern) with { VerticalScrollable = true };
            return ScrollSuccess(
                handle,
                commandId,
                matchedName,
                matchedAutomationId,
                matchedControlType,
                matchedBounds,
                direction,
                amount,
                "keyboard-fallback",
                before,
                keyboardAfter,
                occurrenceIndex,
                changedOverride: keyboardChanged);
        }
        finally
        {
            ReleaseComObject(rawPattern);
            if (!ReferenceEquals(match, root))
                ReleaseComObject(match);
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }
    [SupportedOSPlatform("windows")]
    private static CommandError? PrepareScrollWindow(
        IntPtr handle,
        IUIAutomationElement root,
        bool focusWindow,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!focusWindow)
        {
            return GetForegroundWindow() == handle
                ? null
                : new CommandError(ErrorCodes.UiForegroundMismatch, "The requested window is not foreground and focusWindow is false.");
        }
        if (IsIconic(handle))
        {
            ShowWindowAsync(handle, ShowWindowRestore);
            if (!WaitForWindowState(() => !IsIconic(handle), cancellationToken))
                return new CommandError(ErrorCodes.WindowFocusFailed, "The requested window could not be restored from its minimized state.");
        }
        RequestForegroundActivation(handle);
        root.SetFocus();
        return WaitForWindowState(() => GetForegroundWindow() == handle, cancellationToken)
            ? null
            : new CommandError(ErrorCodes.WindowFocusFailed, "Windows did not grant foreground activation to the requested window.");
    }
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<ScrollItemFallbackCandidate> FindScrollItemFallbackCandidates(
        IUIAutomationElement target,
        IUIAutomationTreeWalker walker,
        UiBounds viewport,
        string direction,
        string amount,
        CancellationToken cancellationToken)
    {
        var context = new ScrollItemSearchContext(viewport, direction, amount, cancellationToken);
        SearchScrollItemCandidates(target, walker, depth: 0, context);
        return context.Candidates
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Ordinal)
            .Take(MaxScrollItemFallbackAttempts)
            .ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static void SearchScrollItemCandidates(
        IUIAutomationElement parent,
        IUIAutomationTreeWalker walker,
        int depth,
        ScrollItemSearchContext context)
    {
        if (context.StopRequested || depth >= MaxScrollFallbackDepth)
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

        while (current is not null && !context.StopRequested)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (++context.VisitedNodes > MaxScrollFallbackVisitedNodes)
            {
                context.StopRequested = true;
                ReleaseComObject(current);
                break;
            }

            IUIAutomationElement? next = null;
            try
            {
                if (SupportsScrollItemPattern(current))
                {
                    var ordinal = context.CandidateOrdinal++;
                    var bounds = ReadBounds(current);
                    var score = GetScrollItemCandidateScore(
                        bounds,
                        context.Viewport,
                        context.Direction,
                        context.Amount);
                    if (score.HasValue)
                    {
                        context.Candidates.Add(new ScrollItemFallbackCandidate(
                            ordinal,
                            bounds,
                            score.Value));
                    }

                    if (context.CandidateOrdinal >= MaxScrollFallbackCandidates)
                        context.StopRequested = true;
                }

                if (!context.StopRequested)
                    SearchScrollItemCandidates(current, walker, depth + 1, context);
                if (!context.StopRequested)
                    next = walker.GetNextSiblingElement(current);
            }
            catch (COMException)
            {
                next = null;
            }
            finally
            {
                ReleaseComObject(current);
            }

            current = next;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IUIAutomationElement? FindScrollItemByOrdinal(
        IUIAutomationElement parent,
        IUIAutomationTreeWalker walker,
        int requestedOrdinal,
        int depth,
        ref int currentOrdinal,
        ref int visited,
        CancellationToken cancellationToken)
    {
        if (depth >= MaxScrollFallbackDepth)
            return null;

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
            if (++visited > MaxScrollFallbackVisitedNodes)
            {
                ReleaseComObject(current);
                return null;
            }

            IUIAutomationElement? next = null;
            var transfer = false;
            try
            {
                if (SupportsScrollItemPattern(current))
                {
                    if (currentOrdinal == requestedOrdinal)
                    {
                        transfer = true;
                        return current;
                    }

                    currentOrdinal++;
                }

                var descendant = FindScrollItemByOrdinal(
                    current,
                    walker,
                    requestedOrdinal,
                    depth + 1,
                    ref currentOrdinal,
                    ref visited,
                    cancellationToken);
                if (descendant is not null)
                    return descendant;

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
    private static bool SupportsScrollItemPattern(IUIAutomationElement element)
    {
        object? pattern = null;
        try
        {
            pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_ScrollItemPatternId);
            return pattern is IUIAutomationScrollItemPattern;
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

    internal static double? GetScrollItemCandidateScore(
        UiBounds bounds,
        UiBounds viewport,
        string direction,
        string amount)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
            return null;

        var contentBandLeft = viewport.X + (viewport.Width * 0.05);
        var contentBandRight = viewport.X + (viewport.Width * 0.95);
        var centerX = bounds.X + (bounds.Width / 2.0);
        if (centerX < contentBandLeft || centerX > contentBandRight)
            return null;

        var itemTop = (double)bounds.Y;
        var itemBottom = bounds.Y + (double)bounds.Height;
        var centerY = bounds.Y + (bounds.Height / 2.0);
        var viewportTop = (double)viewport.Y;
        var viewportBottom = viewport.Y + (double)viewport.Height;
        var movingUp = string.Equals(direction, UiScrollDirections.Up, StringComparison.Ordinal);
        if (movingUp ? itemBottom >= viewportTop - 1.0 : itemTop <= viewportBottom + 1.0)
            return null;

        if (string.Equals(amount, UiScrollAmounts.End, StringComparison.Ordinal))
            return movingUp ? centerY : -centerY;

        var offset = string.Equals(amount, UiScrollAmounts.Small, StringComparison.Ordinal)
            ? Math.Max(32.0, viewport.Height * 0.12)
            : Math.Max(96.0, viewport.Height * 0.80);
        var desiredCenterY = movingUp
            ? viewportTop - offset
            : viewportBottom + offset;
        return Math.Abs(centerY - desiredCenterY);
    }

    internal static string? GetKeyboardScrollKeys(string direction, string amount)
    {
        if (!UiScrollDirections.IsVertical(direction))
            return null;

        return amount switch
        {
            UiScrollAmounts.Small => direction == UiScrollDirections.Up ? "UP" : "DOWN",
            UiScrollAmounts.Page => direction == UiScrollDirections.Up ? "PAGEUP" : "PAGEDOWN",
            UiScrollAmounts.End => direction == UiScrollDirections.Up ? "HOME" : "END",
            _ => null
        };
    }

    private static bool CanUseKeyboardScrollFallback(string controlType) => controlType is
        "Document" or "Pane" or "List" or "Tree" or "Table" or "DataGrid" or "Custom";

    [SupportedOSPlatform("windows")]
    private static CommandError? ApplyKeyboardScroll(
        IUIAutomationElement target,
        string direction,
        string amount)
    {
        var keys = GetKeyboardScrollKeys(direction, amount);
        if (keys is null || !TryParseKeyChord(keys, out var chord, out _))
            return new CommandError(ErrorCodes.UiScrollNotSupported, "Keyboard scrolling is not supported for this direction and amount.");

        try
        {
            target.SetFocus();
        }
        catch (COMException)
        {
            return new CommandError(ErrorCodes.UiScrollFailed, "The matched control could not receive keyboard focus.");
        }

        var inputs = BuildChordInputs(chord);
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (sent == inputs.Length)
            return null;

        ReleaseModifierKeys(chord.Modifiers);
        return new CommandError(ErrorCodes.UiScrollFailed, "Windows did not accept the complete keyboard scroll input.");
    }

    private static CommandResult<UiScrollResult> ScrollSuccess(
        IntPtr handle,
        Guid commandId,
        string name,
        string automationId,
        string controlType,
        UiBounds bounds,
        string direction,
        string amount,
        string method,
        ScrollSnapshot before,
        ScrollSnapshot after,
        int occurrenceIndex,
        bool? changedOverride = null) => new()
    {
        CommandId = commandId,
        Success = true,
        Data = new UiScrollResult
        {
            WindowHandle = FormatWindowHandle(handle),
            Name = name,
            AutomationId = automationId,
            ControlType = controlType,
            Bounds = bounds,
            Direction = direction,
            Amount = amount,
            ScrollMethod = method,
            HorizontalScrollable = after.HorizontalScrollable,
            VerticalScrollable = after.VerticalScrollable,
            HorizontalPercentBefore = before.HorizontalPercent,
            HorizontalPercentAfter = after.HorizontalPercent,
            VerticalPercentBefore = before.VerticalPercent,
            VerticalPercentAfter = after.VerticalPercent,
            HorizontalViewSize = after.HorizontalViewSize,
            VerticalViewSize = after.VerticalViewSize,
            Changed = changedOverride ?? HasScrollChanged(before, after),
            OccurrenceIndex = occurrenceIndex
        }
    };

    [SupportedOSPlatform("windows")]
    private static string ApplyScroll(IUIAutomationScrollPattern pattern, string direction, string amount)
    {
        if (amount == UiScrollAmounts.End)
        {
            var horizontalPercent = direction switch
            {
                UiScrollDirections.Left => 0.0,
                UiScrollDirections.Right => 100.0,
                _ => NoScrollPercent
            };
            var verticalPercent = direction switch
            {
                UiScrollDirections.Up => 0.0,
                UiScrollDirections.Down => 100.0,
                _ => NoScrollPercent
            };
            pattern.SetScrollPercent(horizontalPercent, verticalPercent);
            return "set-scroll-percent";
        }
        var increment = direction is UiScrollDirections.Down or UiScrollDirections.Right;
        var scrollAmount = amount == UiScrollAmounts.Small
            ? increment ? ScrollAmount.ScrollAmount_SmallIncrement : ScrollAmount.ScrollAmount_SmallDecrement
            : increment ? ScrollAmount.ScrollAmount_LargeIncrement : ScrollAmount.ScrollAmount_LargeDecrement;
        var noAmount = ScrollAmount.ScrollAmount_NoAmount;
        pattern.Scroll(
            UiScrollDirections.IsVertical(direction) ? noAmount : scrollAmount,
            UiScrollDirections.IsVertical(direction) ? scrollAmount : noAmount);
        return "scroll-pattern";
    }
    [SupportedOSPlatform("windows")]
    private static ScrollSnapshot ObserveScrollResult(
        IUIAutomationScrollPattern pattern,
        ScrollSnapshot before,
        string direction,
        string amount,
        CancellationToken cancellationToken)
    {
        var current = ReadScrollSnapshot(pattern);
        for (var attempt = 0; attempt < ScrollObservationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasReachedRequestedPosition(before, current, direction, amount))
                return current;
            Thread.Sleep(ScrollObservationDelayMilliseconds);
            current = ReadScrollSnapshot(pattern);
        }
        return current;
    }
    private static bool HasReachedRequestedPosition(
        ScrollSnapshot before,
        ScrollSnapshot current,
        string direction,
        string amount)
    {
        var percent = UiScrollDirections.IsVertical(direction)
            ? current.VerticalPercent
            : current.HorizontalPercent;
        if (amount == UiScrollAmounts.End)
        {
            var expected = direction is UiScrollDirections.Up or UiScrollDirections.Left ? 0.0 : 100.0;
            return percent.HasValue && Math.Abs(percent.Value - expected) <= ScrollPercentEpsilon;
        }
        return HasScrollChanged(before, current);
    }
    private static bool HasScrollChanged(ScrollSnapshot before, ScrollSnapshot after) =>
        PercentChanged(before.HorizontalPercent, after.HorizontalPercent)
        || PercentChanged(before.VerticalPercent, after.VerticalPercent);
    private static bool PercentChanged(double? before, double? after) =>
        before.HasValue != after.HasValue
        || (before.HasValue && after.HasValue && Math.Abs(before.Value - after.Value) > ScrollPercentEpsilon);
    [SupportedOSPlatform("windows")]
    private static ScrollSnapshot ReadScrollSnapshot(IUIAutomationScrollPattern pattern)
    {
        var horizontallyScrollable = ReadOrDefault(() => pattern.CurrentHorizontallyScrollable != 0, false);
        var verticallyScrollable = ReadOrDefault(() => pattern.CurrentVerticallyScrollable != 0, false);
        return new ScrollSnapshot(
            horizontallyScrollable,
            verticallyScrollable,
            horizontallyScrollable ? NormalizeScrollPercent(ReadOrDefault(() => pattern.CurrentHorizontalScrollPercent, NoScrollPercent)) : null,
            verticallyScrollable ? NormalizeScrollPercent(ReadOrDefault(() => pattern.CurrentVerticalScrollPercent, NoScrollPercent)) : null,
            ReadOrDefault(() => pattern.CurrentHorizontalViewSize, 100.0),
            ReadOrDefault(() => pattern.CurrentVerticalViewSize, 100.0));
    }
    private static double? NormalizeScrollPercent(double value) =>
        value < 0 ? null : Math.Clamp(value, 0.0, 100.0);
    private static CommandResult<UiScrollResult> ScrollFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };
    private readonly record struct ScrollSnapshot(
        bool HorizontalScrollable,
        bool VerticalScrollable,
        double? HorizontalPercent,
        double? VerticalPercent,
        double HorizontalViewSize,
        double VerticalViewSize);

    [SupportedOSPlatform("windows")]
    private static ScrollViewportSnapshot CaptureScrollViewportSnapshot(
        IUIAutomationElement target,
        IUIAutomationTreeWalker walker,
        UiBounds viewport,
        CancellationToken cancellationToken)
    {
        var context = new ScrollVerificationSearchContext(viewport, cancellationToken);
        SearchScrollVerificationMarkers(target, walker, depth: 0, context);
        var markers = context.Markers
            .OrderBy(marker => marker.Key, StringComparer.Ordinal)
            .ThenBy(marker => marker.Y)
            .Take(MaxScrollVerificationMarkers)
            .ToArray();
        return new ScrollViewportSnapshot(markers);
    }

    [SupportedOSPlatform("windows")]
    private static void SearchScrollVerificationMarkers(
        IUIAutomationElement parent,
        IUIAutomationTreeWalker walker,
        int depth,
        ScrollVerificationSearchContext context)
    {
        if (context.StopRequested || depth >= MaxScrollFallbackDepth)
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

        while (current is not null && !context.StopRequested)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (++context.VisitedNodes > MaxScrollFallbackVisitedNodes)
            {
                context.StopRequested = true;
                ReleaseComObject(current);
                break;
            }

            IUIAutomationElement? next = null;
            try
            {
                var bounds = ReadBounds(current);
                if (IsScrollVerificationMarker(bounds, context.Viewport))
                {
                    var name = LimitMetadata(ReadOrDefault(() => current.CurrentName, string.Empty));
                    var automationId = LimitMetadata(ReadOrDefault(() => current.CurrentAutomationId, string.Empty));
                    if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(automationId))
                    {
                        var controlType = GetControlTypeName(ReadOrDefault(() => current.CurrentControlType, 0));
                        var key = string.Concat(controlType, "\u001f", automationId, "\u001f", name);
                        context.Markers.Add(new ScrollVerificationMarker(key, bounds.Y, bounds.Height));
                    }
                }

                if (context.Markers.Count >= MaxScrollVerificationMarkers * 4)
                    context.StopRequested = true;
                if (!context.StopRequested)
                    SearchScrollVerificationMarkers(current, walker, depth + 1, context);
                if (!context.StopRequested)
                    next = walker.GetNextSiblingElement(current);
            }
            catch (COMException)
            {
                next = null;
            }
            finally
            {
                ReleaseComObject(current);
            }

            current = next;
        }
    }

    private static bool IsScrollVerificationMarker(UiBounds bounds, UiBounds viewport)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
            return false;
        if (bounds.Height >= viewport.Height * 0.9)
            return false;

        var centerX = bounds.X + (bounds.Width / 2.0);
        var contentBandLeft = viewport.X + (viewport.Width * 0.05);
        var contentBandRight = viewport.X + (viewport.Width * 0.95);
        if (centerX < contentBandLeft || centerX > contentBandRight)
            return false;

        var itemBottom = bounds.Y + (double)bounds.Height;
        var viewportBottom = viewport.Y + (double)viewport.Height;
        return itemBottom >= viewport.Y && bounds.Y <= viewportBottom;
    }

    [SupportedOSPlatform("windows")]
    private static bool ObserveViewportMovement(
        IUIAutomationElement target,
        IUIAutomationTreeWalker walker,
        UiBounds viewport,
        ScrollViewportSnapshot before,
        string direction,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ScrollObservationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = CaptureScrollViewportSnapshot(target, walker, viewport, cancellationToken);
            if (HasViewportSnapshotMoved(before.Markers, current.Markers, direction))
                return true;

            Thread.Sleep(ScrollObservationDelayMilliseconds);
        }

        return false;
    }

    internal static bool HasViewportSnapshotMoved(
        IReadOnlyList<ScrollVerificationMarker> before,
        IReadOnlyList<ScrollVerificationMarker> after,
        string direction)
    {
        if (before.Count == 0 || after.Count == 0)
            return false;

        var movingUp = string.Equals(direction, UiScrollDirections.Up, StringComparison.Ordinal);
        var beforeGroups = before.GroupBy(marker => marker.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(marker => marker.Y).ToArray(), StringComparer.Ordinal);
        var afterGroups = after.GroupBy(marker => marker.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(marker => marker.Y).ToArray(), StringComparer.Ordinal);

        var directionalMoves = 0;
        var strongDirectionalMoves = 0;
        foreach (var (key, beforeMarkers) in beforeGroups)
        {
            if (!afterGroups.TryGetValue(key, out var afterMarkers))
                continue;

            var pairCount = Math.Min(beforeMarkers.Length, afterMarkers.Length);
            for (var index = 0; index < pairCount; index++)
            {
                var delta = afterMarkers[index].Y - beforeMarkers[index].Y;
                var directionMatches = movingUp
                    ? delta > ScrollBoundsMovementTolerance
                    : delta < -ScrollBoundsMovementTolerance;
                if (!directionMatches)
                    continue;

                directionalMoves++;
                if (Math.Abs(delta) >= StrongScrollBoundsMovement)
                    strongDirectionalMoves++;
            }
        }

        if (directionalMoves >= 2 || strongDirectionalMoves >= 1)
            return true;

        var beforeKeys = beforeGroups.Keys.ToHashSet(StringComparer.Ordinal);
        var afterKeys = afterGroups.Keys.ToHashSet(StringComparer.Ordinal);
        if (beforeKeys.Count < 3 || afterKeys.Count < 3)
            return false;

        var intersectionCount = beforeKeys.Count(key => afterKeys.Contains(key));
        var unionCount = beforeKeys.Count + afterKeys.Count - intersectionCount;
        var similarity = unionCount == 0 ? 1.0 : intersectionCount / (double)unionCount;
        return similarity <= 0.50;
    }

    private readonly record struct ScrollItemFallbackCandidate(
        int Ordinal,
        UiBounds Bounds,
        double Score);

    internal readonly record struct ScrollVerificationMarker(
        string Key,
        double Y,
        double Height);

    private readonly record struct ScrollViewportSnapshot(
        IReadOnlyList<ScrollVerificationMarker> Markers);

    private sealed class ScrollVerificationSearchContext
    {
        public ScrollVerificationSearchContext(
            UiBounds viewport,
            CancellationToken cancellationToken)
        {
            Viewport = viewport;
            CancellationToken = cancellationToken;
        }

        public UiBounds Viewport { get; }
        public CancellationToken CancellationToken { get; }
        public int VisitedNodes { get; set; }
        public List<ScrollVerificationMarker> Markers { get; } = [];
        public bool StopRequested { get; set; }
    }

    private sealed class ScrollItemSearchContext
    {
        public ScrollItemSearchContext(
            UiBounds viewport,
            string direction,
            string amount,
            CancellationToken cancellationToken)
        {
            Viewport = viewport;
            Direction = direction;
            Amount = amount;
            CancellationToken = cancellationToken;
        }

        public UiBounds Viewport { get; }
        public string Direction { get; }
        public string Amount { get; }
        public CancellationToken CancellationToken { get; }
        public int VisitedNodes { get; set; }
        public int CandidateOrdinal { get; set; }
        public List<ScrollItemFallbackCandidate> Candidates { get; } = [];
        public bool StopRequested { get; set; }
    }
}

internal static class AdditionalUiDispatch
{
    public static Task<CommandResult<UiScrollResult>> HandleAsync(
        IUiAutomationExecutor? executor,
        UiScrollCommand command,
        CancellationToken cancellationToken)
    {
        if (executor is UiAutomationExecutor concrete)
        {
            return concrete.ScrollAsync(
                command.WindowHandle,
                command.Direction,
                command.Amount,
                command.AutomationId,
                command.Name,
                command.ControlType,
                command.OccurrenceIndex,
                command.FocusWindow,
                command.CommandId,
                cancellationToken);
        }

        return Task.FromResult(new CommandResult<UiScrollResult>
        {
            CommandId = command.CommandId,
            Success = false,
            Error = new CommandError(ErrorCodes.UiAutomationUnavailable, "UI automation is not configured on this agent.")
        });
    }
}
