namespace LocalMcp.Contracts.Results;

public sealed record WindowListResult
{
    public required IReadOnlyList<WindowInfo> Windows { get; init; }
    public int Count { get; init; }
    public int MaxWindows { get; init; }
    public bool Truncated { get; init; }
}

public sealed record WindowInfo
{
    public required string WindowHandle { get; init; }
    public required string WindowHandleDecimal { get; init; }
    public required string Title { get; init; }
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string ClassName { get; init; }
    public required UiBounds Bounds { get; init; }
    public bool IsVisible { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsMinimized { get; init; }
    public bool IsMaximized { get; init; }
    public bool IsForeground { get; init; }
    public bool IsCloaked { get; init; }
    public int ZOrder { get; init; }
}
