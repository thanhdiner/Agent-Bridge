namespace LocalMcp.Contracts.Results;

public sealed record FileDialogSetPathResult
{
    public required string WindowHandle { get; init; }
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public int OccurrenceIndex { get; init; }
    public int PathLength { get; init; }
    public bool Verified { get; init; }
    public bool Submitted { get; init; }
}
