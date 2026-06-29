namespace LocalMcp.Contracts.Results;

public sealed record ScreenInputGuardInfo
{
    public required string ExpectedForegroundWindowHandle { get; init; }
    public required string ActualForegroundWindowHandle { get; init; }
    public required string ExpectedRootOwnerWindowHandle { get; init; }
    public required string ActualRootOwnerWindowHandle { get; init; }
    public required string Title { get; init; }
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
}

public sealed record ScreenClickResult
{
    public required ScreenInputGuardInfo Guard { get; init; }
    public required UiBounds VirtualScreenBounds { get; init; }
    public int MonitorIndex { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public required string HitWindowHandle { get; init; }
    public required string HitRootOwnerWindowHandle { get; init; }
    public required string Button { get; init; }
    public int ClickCount { get; init; }
    public bool Clicked { get; init; }
}

public sealed record ScreenDragResult
{
    public required ScreenInputGuardInfo Guard { get; init; }
    public required UiBounds VirtualScreenBounds { get; init; }
    public int StartMonitorIndex { get; init; }
    public int EndMonitorIndex { get; init; }
    public int StartX { get; init; }
    public int StartY { get; init; }
    public int EndX { get; init; }
    public int EndY { get; init; }
    public required string HitWindowHandle { get; init; }
    public required string HitRootOwnerWindowHandle { get; init; }
    public required string Button { get; init; }
    public int DurationMs { get; init; }
    public int Steps { get; init; }
    public bool Dragged { get; init; }
}

public sealed record ScreenScrollResult
{
    public required ScreenInputGuardInfo Guard { get; init; }
    public required UiBounds VirtualScreenBounds { get; init; }
    public int MonitorIndex { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public required string HitWindowHandle { get; init; }
    public required string HitRootOwnerWindowHandle { get; init; }
    public required string Direction { get; init; }
    public int Notches { get; init; }
    public int WheelDelta { get; init; }
    public bool Scrolled { get; init; }
}
