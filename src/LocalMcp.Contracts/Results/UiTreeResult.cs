namespace LocalMcp.Contracts.Results;

public sealed record UiTreeResult
{
    public required string WindowHandle { get; init; }
    public int ProcessId { get; init; }
    public required UiTreeNode Root { get; init; }
    public int NodeCount { get; init; }
    public int MaxDepth { get; init; }
    public int MaxNodes { get; init; }
    public bool Truncated { get; init; }
}

public sealed record UiTreeNode
{
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public bool Enabled { get; init; }
    public bool IsPassword { get; init; }
    public string? Value { get; init; }
    public bool ValueTruncated { get; init; }
    public required List<UiTreeNode> Children { get; init; }
}

public sealed record UiBounds
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}
