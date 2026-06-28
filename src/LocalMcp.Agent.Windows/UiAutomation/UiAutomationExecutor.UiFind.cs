using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const int MaxUiFindVisitedNodes = 5_000;

    private static readonly (int PatternId, string Name)[] UiFindPatterns =
    [
        (UIA_PatternIds.UIA_InvokePatternId, "invoke"),
        (UIA_PatternIds.UIA_SelectionItemPatternId, "selection-item"),
        (UIA_PatternIds.UIA_TogglePatternId, "toggle"),
        (UIA_PatternIds.UIA_ValuePatternId, "value"),
        (UIA_PatternIds.UIA_RangeValuePatternId, "range-value"),
        (UIA_PatternIds.UIA_TextPatternId, "text"),
        (UIA_PatternIds.UIA_LegacyIAccessiblePatternId, "legacy-accessible"),
        (UIA_PatternIds.UIA_WindowPatternId, "window"),
        (UIA_PatternIds.UIA_TransformPatternId, "transform"),
        (UIA_PatternIds.UIA_ScrollPatternId, "scroll"),
        (UIA_PatternIds.UIA_ScrollItemPatternId, "scroll-item"),
        (UIA_PatternIds.UIA_ExpandCollapsePatternId, "expand-collapse")
    ];

    public async Task<CommandResult<UiFindResult>> FindAsync(
        string windowHandle,
        string? automationId,
        string? nameContains,
        string? controlType,
        int maxDepth,
        int maxResults,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return FindFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrWhiteSpace(automationId)
            && string.IsNullOrWhiteSpace(nameContains)
            && string.IsNullOrWhiteSpace(controlType))
            return FindFailure(commandId, ErrorCodes.InvalidRequest, "automationId, nameContains, or controlType is required.");
        if (maxDepth is < 0 or > 20)
            return FindFailure(commandId, ErrorCodes.InvalidRequest, "maxDepth must be between 0 and 20.");
        if (maxResults is < 1 or > 100)
            return FindFailure(commandId, ErrorCodes.InvalidRequest, "maxResults must be between 1 and 100.");
        if (automationId?.Length > 1024 || nameContains?.Length > 1024 || controlType?.Length > 128)
            return FindFailure(commandId, ErrorCodes.InvalidRequest, "automationId and nameContains must be at most 1024 characters; controlType must be at most 128 characters.");
        if (!OperatingSystem.IsWindows())
            return FindFailure(commandId, ErrorCodes.UiAutomationUnavailable, "UI control search is only available on Windows agents.");

        try
        {
            return await Task.Run(
                () => FindWindows(
                    handle,
                    automationId?.Trim(),
                    nameContains?.Trim(),
                    controlType?.Trim(),
                    maxDepth,
                    maxResults,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return FindFailure(commandId, ErrorCodes.CommandCancelled, "The UI control search was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation search failure for command {CommandId}", commandId);
            return FindFailure(commandId, ErrorCodes.UiAutomationFailed, "Windows UI Automation could not search the requested window.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI control search failure for command {CommandId}", commandId);
            return FindFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while searching UI controls.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiFindResult> FindWindows(
        IntPtr handle,
        string? automationId,
        string? nameContains,
        string? controlType,
        int maxDepth,
        int maxResults,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return FindFailure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;

        try
        {
            automation = new CUIAutomation8();
            if (automation is IUIAutomation2 automation2)
            {
                automation2.ConnectionTimeout = 5_000;
                automation2.TransactionTimeout = 5_000;
            }

            root = automation.ElementFromHandle(handle);
            if (root is null)
                return FindFailure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");

            walker = automation.ControlViewWalker;
            var context = new UiFindContext(maxDepth, maxResults, cancellationToken);
            TraverseForMatches(root, walker, 0, automationId, nameContains, controlType, context);

            return new CommandResult<UiFindResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new UiFindResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    ProcessId = ReadOrDefault(() => root.CurrentProcessId, 0),
                    Matches = context.Matches,
                    Count = context.Matches.Count,
                    VisitedNodes = context.VisitedNodes,
                    MaxDepth = maxDepth,
                    MaxResults = maxResults,
                    Truncated = context.Truncated
                }
            };
        }
        finally
        {
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TraverseForMatches(
        IUIAutomationElement element,
        IUIAutomationTreeWalker walker,
        int depth,
        string? automationId,
        string? nameContains,
        string? controlType,
        UiFindContext context)
    {
        if (context.StopRequested)
            return;

        context.CancellationToken.ThrowIfCancellationRequested();
        context.VisitedNodes++;
        if (context.VisitedNodes > MaxUiFindVisitedNodes)
        {
            context.Truncated = true;
            context.StopRequested = true;
            return;
        }

        var name = LimitMetadata(ReadOrDefault(() => element.CurrentName, string.Empty));
        var matchedAutomationId = LimitMetadata(ReadOrDefault(() => element.CurrentAutomationId, string.Empty));
        var matchedControlType = GetControlTypeName(ReadOrDefault(() => element.CurrentControlType, 0));

        if (MatchesFindSelector(name, matchedAutomationId, matchedControlType, automationId, nameContains, controlType))
        {
            context.ControlTypeOccurrences.TryGetValue(matchedControlType, out var controlTypeOccurrenceIndex);
            context.ControlTypeOccurrences[matchedControlType] = controlTypeOccurrenceIndex + 1;

            int occurrenceIndex;
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(matchedAutomationId))
            {
                occurrenceIndex = controlTypeOccurrenceIndex;
            }
            else
            {
                var key = CreateFindOccurrenceKey(
                    name,
                    matchedAutomationId,
                    matchedControlType,
                    preferAutomationId: !string.IsNullOrEmpty(automationId));
                context.Occurrences.TryGetValue(key, out occurrenceIndex);
                context.Occurrences[key] = occurrenceIndex + 1;
            }

            context.Matches.Add(new UiFindMatch
            {
                Name = name,
                AutomationId = matchedAutomationId,
                ControlType = matchedControlType,
                Bounds = ReadBounds(element),
                Enabled = ReadOrDefault(() => element.CurrentIsEnabled != 0, false),
                Patterns = ReadSupportedPatterns(element),
                OccurrenceIndex = occurrenceIndex,
                Depth = depth
            });

            if (context.Matches.Count >= context.MaxResults)
            {
                context.Truncated = true;
                context.StopRequested = true;
                return;
            }
        }

        if (depth >= context.MaxDepth)
        {
            MarkDepthTruncation(element, walker, context);
            return;
        }

        IUIAutomationElement? current = null;
        try
        {
            current = walker.GetFirstChildElement(element);
        }
        catch (COMException)
        {
            context.Truncated = true;
            return;
        }

        while (current is not null && !context.StopRequested)
        {
            IUIAutomationElement? next = null;
            try
            {
                TraverseForMatches(current, walker, depth + 1, automationId, nameContains, controlType, context);
                if (!context.StopRequested)
                    next = walker.GetNextSiblingElement(current);
            }
            catch (COMException)
            {
                context.Truncated = true;
            }
            finally
            {
                ReleaseComObject(current);
            }

            current = next;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void MarkDepthTruncation(
        IUIAutomationElement element,
        IUIAutomationTreeWalker walker,
        UiFindContext context)
    {
        IUIAutomationElement? child = null;
        try
        {
            child = walker.GetFirstChildElement(element);
            if (child is not null)
                context.Truncated = true;
        }
        catch (COMException)
        {
            context.Truncated = true;
        }
        finally
        {
            ReleaseComObject(child);
        }
    }

    internal static string CreateFindOccurrenceKey(
        string name,
        string automationId,
        string controlType,
        bool preferAutomationId)
    {
        var useAutomationId = preferAutomationId || string.IsNullOrEmpty(name);
        var selectorKind = useAutomationId ? "automation-id" : "name";
        var selectorValue = useAutomationId ? automationId : name;

        return string.Concat(
            selectorKind,
            ":",
            selectorValue.Length,
            ":",
            selectorValue,
            "|control-type:",
            controlType.Length,
            ":",
            controlType);
    }

    private static bool MatchesFindSelector(
        string name,
        string automationIdValue,
        string controlTypeValue,
        string? automationId,
        string? nameContains,
        string? controlType)
    {
        if (!string.IsNullOrEmpty(automationId)
            && !string.Equals(automationIdValue, automationId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(nameContains)
            && !name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(controlType)
            && !string.Equals(controlTypeValue, controlType, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static List<string> ReadSupportedPatterns(IUIAutomationElement element)
    {
        var patterns = new List<string>();
        foreach (var (patternId, name) in UiFindPatterns)
        {
            object? pattern = null;
            try
            {
                pattern = element.GetCurrentPattern(patternId);
                if (pattern is not null)
                    patterns.Add(name);
            }
            catch (COMException)
            {
            }
            finally
            {
                ReleaseComObject(pattern);
            }
        }

        return patterns;
    }

    private static CommandResult<UiFindResult> FindFailure(Guid commandId, string code, string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };

    private sealed class UiFindContext
    {
        public UiFindContext(int maxDepth, int maxResults, CancellationToken cancellationToken)
        {
            MaxDepth = maxDepth;
            MaxResults = maxResults;
            CancellationToken = cancellationToken;
        }

        public int MaxDepth { get; }
        public int MaxResults { get; }
        public CancellationToken CancellationToken { get; }
        public List<UiFindMatch> Matches { get; } = [];
        public Dictionary<string, int> Occurrences { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ControlTypeOccurrences { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int VisitedNodes { get; set; }
        public bool Truncated { get; set; }
        public bool StopRequested { get; set; }
    }
}
