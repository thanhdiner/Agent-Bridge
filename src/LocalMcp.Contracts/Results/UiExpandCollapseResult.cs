namespace LocalMcp.Contracts.Results;

public sealed record UiExpandCollapseResult
{
    public required string WindowHandle { get; init; }
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public required string Action { get; init; }
    public required string ExpandCollapseMethod { get; init; }
    public required string StateBefore { get; init; }
    public required string StateAfter { get; init; }
    public bool Verified { get; init; }
    public bool ScrolledIntoView { get; init; }
    public int OccurrenceIndex { get; init; }
}

public static class UiExpandCollapseStates
{
    public const string Collapsed = "collapsed";
    public const string Expanded = "expanded";
    public const string PartiallyExpanded = "partially-expanded";
    public const string LeafNode = "leaf-node";
    public const string Unknown = "unknown";
}
