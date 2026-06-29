namespace LocalMcp.Contracts.Results;

public sealed record WindowDragResult
{
    public required string WindowHandle { get; init; }
    public required string Title { get; init; }
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required UiBounds InitialBounds { get; init; }
    public required UiBounds FinalBounds { get; init; }
    public int StartX { get; init; }
    public int StartY { get; init; }
    public int EndX { get; init; }
    public int EndY { get; init; }
    public int StartScreenX { get; init; }
    public int StartScreenY { get; init; }
    public int EndScreenX { get; init; }
    public int EndScreenY { get; init; }
    public required string Button { get; init; }
    public int DurationMs { get; init; }
    public int Steps { get; init; }
    public bool WasMinimized { get; init; }
    public bool Restored { get; init; }
    public required string PreviousForegroundWindow { get; init; }
    public bool IsForeground { get; init; }
    public bool Dragged { get; init; }
}
