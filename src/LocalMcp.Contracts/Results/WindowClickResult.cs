namespace LocalMcp.Contracts.Results;
public sealed record WindowClickResult
{
    public required string WindowHandle { get; init; }
    public required string Title { get; init; }
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required UiBounds Bounds { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int ScreenX { get; init; }
    public int ScreenY { get; init; }
    public required string Button { get; init; }
    public int ClickCount { get; init; }
    public bool WasMinimized { get; init; }
    public bool Restored { get; init; }
    public required string PreviousForegroundWindow { get; init; }
    public bool IsForeground { get; init; }
    public bool Clicked { get; init; }
}
