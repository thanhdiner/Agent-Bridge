using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;
using Microsoft.Extensions.Logging;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed class UiAutomationExecutor : IUiAutomationExecutor
{
    private const int MaxValueCharacters = 4096;
    private readonly ILogger<UiAutomationExecutor> _logger;

    public UiAutomationExecutor(ILogger<UiAutomationExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<CommandResult<UiTreeResult>> GetTreeAsync(
        string windowHandle,
        int maxDepth,
        int maxNodes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (maxDepth is < 0 or > 20)
            return Failure(commandId, ErrorCodes.InvalidRequest, "maxDepth must be between 0 and 20.");
        if (maxNodes is < 1 or > 1000)
            return Failure(commandId, ErrorCodes.InvalidRequest, "maxNodes must be between 1 and 1000.");
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return Failure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (!OperatingSystem.IsWindows())
            return Failure(commandId, ErrorCodes.UiAutomationUnavailable, "Windows UI Automation is only available on Windows agents.");

        try
        {
            return await RunWindowsTreeAsync(
                handle,
                maxDepth,
                maxNodes,
                commandId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure(commandId, ErrorCodes.CommandCancelled, "The UI tree request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation COM failure for command {CommandId}", commandId);
            return Failure(commandId, ErrorCodes.UiAutomationFailed, "Windows UI Automation could not read the requested window.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected UI tree failure for command {CommandId}", commandId);
            return Failure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while reading the UI tree.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static Task<CommandResult<UiTreeResult>> RunWindowsTreeAsync(
        IntPtr handle,
        int maxDepth,
        int maxNodes,
        Guid commandId,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => GetTreeWindows(handle, maxDepth, maxNodes, commandId, cancellationToken),
            cancellationToken);

    [SupportedOSPlatform("windows")]
    private static CommandResult<UiTreeResult> GetTreeWindows(
        IntPtr handle,
        int maxDepth,
        int maxNodes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle))
            return Failure(commandId, ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");

        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? rootElement = null;

        try
        {
            automation = new CUIAutomation8();
            if (automation is IUIAutomation2 automation2)
            {
                automation2.ConnectionTimeout = 5000;
                automation2.TransactionTimeout = 5000;
            }

            rootElement = automation.ElementFromHandle(handle);
            if (rootElement is null)
                return Failure(commandId, ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");

            walker = automation.ControlViewWalker;
            var context = new TraversalContext(maxDepth, maxNodes, cancellationToken);
            var root = ReadNode(rootElement, walker, depth: 0, context);
            var processId = ReadOrDefault(() => rootElement.CurrentProcessId, 0);

            return new CommandResult<UiTreeResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new UiTreeResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    ProcessId = processId,
                    Root = root,
                    NodeCount = context.NodeCount,
                    MaxDepth = maxDepth,
                    MaxNodes = maxNodes,
                    Truncated = context.Truncated
                }
            };
        }
        finally
        {
            ReleaseComObject(rootElement);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }

    [SupportedOSPlatform("windows")]
    private static UiTreeNode ReadNode(
        IUIAutomationElement element,
        IUIAutomationTreeWalker walker,
        int depth,
        TraversalContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        context.NodeCount++;

        var isPassword = ReadOrDefault(() => element.CurrentIsPassword != 0, false);
        var value = ReadValue(element, isPassword, out var valueTruncated);
        var children = new List<UiTreeNode>();

        if (depth >= context.MaxDepth)
        {
            IUIAutomationElement? omittedChild = null;
            try
            {
                omittedChild = walker.GetFirstChildElement(element);
                if (omittedChild is not null)
                    context.Truncated = true;
            }
            catch (COMException)
            {
                context.Truncated = true;
            }
            finally
            {
                ReleaseComObject(omittedChild);
            }
        }
        else
        {
            ReadChildren(element, walker, depth, context, children);
        }

        return new UiTreeNode
        {
            Name = LimitMetadata(ReadOrDefault(() => element.CurrentName, string.Empty)),
            AutomationId = LimitMetadata(ReadOrDefault(() => element.CurrentAutomationId, string.Empty)),
            ControlType = GetControlTypeName(ReadOrDefault(() => element.CurrentControlType, 0)),
            Bounds = ReadBounds(element),
            Enabled = ReadOrDefault(() => element.CurrentIsEnabled != 0, false),
            IsPassword = isPassword,
            Value = value,
            ValueTruncated = valueTruncated,
            Children = children
        };
    }

    [SupportedOSPlatform("windows")]
    private static void ReadChildren(
        IUIAutomationElement parent,
        IUIAutomationTreeWalker walker,
        int parentDepth,
        TraversalContext context,
        List<UiTreeNode> destination)
    {
        IUIAutomationElement? current = null;
        try
        {
            current = walker.GetFirstChildElement(parent);
        }
        catch (COMException)
        {
            context.Truncated = true;
            return;
        }

        while (current is not null)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (context.NodeCount >= context.MaxNodes)
            {
                context.Truncated = true;
                ReleaseComObject(current);
                break;
            }

            IUIAutomationElement? next = null;
            try
            {
                destination.Add(ReadNode(current, walker, parentDepth + 1, context));
                next = walker.GetNextSiblingElement(current);
            }
            catch (COMException)
            {
                context.Truncated = true;
                try
                {
                    next = walker.GetNextSiblingElement(current);
                }
                catch (COMException)
                {
                    next = null;
                }
            }
            finally
            {
                ReleaseComObject(current);
            }

            current = next;
        }
    }

    [SupportedOSPlatform("windows")]
    private static UiBounds ReadBounds(IUIAutomationElement element)
    {
        try
        {
            var rectangle = element.CurrentBoundingRectangle;
            return new UiBounds
            {
                X = rectangle.left,
                Y = rectangle.top,
                Width = Math.Max(0, rectangle.right - rectangle.left),
                Height = Math.Max(0, rectangle.bottom - rectangle.top)
            };
        }
        catch (COMException)
        {
            return new UiBounds();
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadValue(
        IUIAutomationElement element,
        bool isPassword,
        out bool truncated)
    {
        truncated = false;
        if (isPassword)
            return null;

        var value = TryReadValuePattern(element)
            ?? TryReadRangeValuePattern(element)
            ?? TryReadLegacyValuePattern(element)
            ?? TryReadTextPattern(element);

        if (value is null)
            return null;

        if (value.Length <= MaxValueCharacters)
            return value;

        truncated = true;
        return value[..MaxValueCharacters];
    }

    [SupportedOSPlatform("windows")]
    private static string? TryReadValuePattern(IUIAutomationElement element)
    {
        object? pattern = null;
        try
        {
            pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId);
            return pattern is IUIAutomationValuePattern valuePattern
                ? valuePattern.CurrentValue
                : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(pattern);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? TryReadRangeValuePattern(IUIAutomationElement element)
    {
        object? pattern = null;
        try
        {
            pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_RangeValuePatternId);
            return pattern is IUIAutomationRangeValuePattern rangePattern
                ? rangePattern.CurrentValue.ToString("G17", CultureInfo.InvariantCulture)
                : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(pattern);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? TryReadLegacyValuePattern(IUIAutomationElement element)
    {
        object? pattern = null;
        try
        {
            pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_LegacyIAccessiblePatternId);
            return pattern is IUIAutomationLegacyIAccessiblePattern legacyPattern
                ? legacyPattern.CurrentValue
                : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(pattern);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? TryReadTextPattern(IUIAutomationElement element)
    {
        object? pattern = null;
        IUIAutomationTextRange? range = null;
        try
        {
            pattern = element.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId);
            if (pattern is not IUIAutomationTextPattern textPattern)
                return null;

            range = textPattern.DocumentRange;
            return range?.GetText(MaxValueCharacters + 1);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(range);
            ReleaseComObject(pattern);
        }
    }

    internal static bool TryParseWindowHandle(string? value, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        ulong raw;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out raw))
                return false;
        }
        else if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out raw))
        {
            return false;
        }

        if (raw == 0 || raw > long.MaxValue)
            return false;

        handle = new IntPtr((long)raw);
        return true;
    }

    internal static string GetControlTypeName(int controlTypeId) => controlTypeId switch
    {
        UIA_ControlTypeIds.UIA_ButtonControlTypeId => "Button",
        UIA_ControlTypeIds.UIA_CalendarControlTypeId => "Calendar",
        UIA_ControlTypeIds.UIA_CheckBoxControlTypeId => "CheckBox",
        UIA_ControlTypeIds.UIA_ComboBoxControlTypeId => "ComboBox",
        UIA_ControlTypeIds.UIA_EditControlTypeId => "Edit",
        UIA_ControlTypeIds.UIA_HyperlinkControlTypeId => "Hyperlink",
        UIA_ControlTypeIds.UIA_ImageControlTypeId => "Image",
        UIA_ControlTypeIds.UIA_ListItemControlTypeId => "ListItem",
        UIA_ControlTypeIds.UIA_ListControlTypeId => "List",
        UIA_ControlTypeIds.UIA_MenuControlTypeId => "Menu",
        UIA_ControlTypeIds.UIA_MenuBarControlTypeId => "MenuBar",
        UIA_ControlTypeIds.UIA_MenuItemControlTypeId => "MenuItem",
        UIA_ControlTypeIds.UIA_ProgressBarControlTypeId => "ProgressBar",
        UIA_ControlTypeIds.UIA_RadioButtonControlTypeId => "RadioButton",
        UIA_ControlTypeIds.UIA_ScrollBarControlTypeId => "ScrollBar",
        UIA_ControlTypeIds.UIA_SliderControlTypeId => "Slider",
        UIA_ControlTypeIds.UIA_SpinnerControlTypeId => "Spinner",
        UIA_ControlTypeIds.UIA_StatusBarControlTypeId => "StatusBar",
        UIA_ControlTypeIds.UIA_TabControlTypeId => "Tab",
        UIA_ControlTypeIds.UIA_TabItemControlTypeId => "TabItem",
        UIA_ControlTypeIds.UIA_TextControlTypeId => "Text",
        UIA_ControlTypeIds.UIA_ToolBarControlTypeId => "ToolBar",
        UIA_ControlTypeIds.UIA_ToolTipControlTypeId => "ToolTip",
        UIA_ControlTypeIds.UIA_TreeControlTypeId => "Tree",
        UIA_ControlTypeIds.UIA_TreeItemControlTypeId => "TreeItem",
        UIA_ControlTypeIds.UIA_CustomControlTypeId => "Custom",
        UIA_ControlTypeIds.UIA_GroupControlTypeId => "Group",
        UIA_ControlTypeIds.UIA_ThumbControlTypeId => "Thumb",
        UIA_ControlTypeIds.UIA_DataGridControlTypeId => "DataGrid",
        UIA_ControlTypeIds.UIA_DataItemControlTypeId => "DataItem",
        UIA_ControlTypeIds.UIA_DocumentControlTypeId => "Document",
        UIA_ControlTypeIds.UIA_SplitButtonControlTypeId => "SplitButton",
        UIA_ControlTypeIds.UIA_WindowControlTypeId => "Window",
        UIA_ControlTypeIds.UIA_PaneControlTypeId => "Pane",
        UIA_ControlTypeIds.UIA_HeaderControlTypeId => "Header",
        UIA_ControlTypeIds.UIA_HeaderItemControlTypeId => "HeaderItem",
        UIA_ControlTypeIds.UIA_TableControlTypeId => "Table",
        UIA_ControlTypeIds.UIA_TitleBarControlTypeId => "TitleBar",
        UIA_ControlTypeIds.UIA_SeparatorControlTypeId => "Separator",
        UIA_ControlTypeIds.UIA_SemanticZoomControlTypeId => "SemanticZoom",
        UIA_ControlTypeIds.UIA_AppBarControlTypeId => "AppBar",
        _ => controlTypeId == 0 ? "Unknown" : $"Unknown({controlTypeId})"
    };

    private static string LimitMetadata(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Length <= 1024 ? value : value[..1024];
    }

    private static T ReadOrDefault<T>(Func<T> reader, T defaultValue)
    {
        try
        {
            return reader();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return defaultValue;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;

        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch (InvalidComObjectException)
        {
        }
    }

    private static string FormatWindowHandle(IntPtr handle) =>
        $"0x{unchecked((ulong)handle.ToInt64()):X}";

    private static CommandResult<UiTreeResult> Failure(Guid commandId, string code, string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    private sealed class TraversalContext
    {
        public TraversalContext(int maxDepth, int maxNodes, CancellationToken cancellationToken)
        {
            MaxDepth = maxDepth;
            MaxNodes = maxNodes;
            CancellationToken = cancellationToken;
        }

        public int MaxDepth { get; }
        public int MaxNodes { get; }
        public CancellationToken CancellationToken { get; }
        public int NodeCount { get; set; }
        public bool Truncated { get; set; }
    }
}
