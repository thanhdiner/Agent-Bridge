namespace LocalMcp.Contracts.Results;

public sealed record UiFocusResult
{
    public required string WindowHandle { get; init; }
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public bool Enabled { get; init; }
    public bool KeyboardFocusable { get; init; }
    public bool FocusedBefore { get; init; }
    public bool FocusedAfter { get; init; }
    public required string FocusMethod { get; init; }
    public bool Verified { get; init; }
    public bool ScrolledIntoView { get; init; }
    public bool WasMinimized { get; init; }
    public bool Restored { get; init; }
    public bool IsForeground { get; init; }
    public int OccurrenceIndex { get; init; }
}
