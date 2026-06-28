namespace LocalMcp.Contracts.Results;
public sealed record UiToggleResult
{
    public required string WindowHandle { get; init; }
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public required string Action { get; init; }
    public required string ToggleMethod { get; init; }
    public required string StateBefore { get; init; }
    public required string StateAfter { get; init; }
    public bool Verified { get; init; }
    public bool ScrolledIntoView { get; init; }
    public int OccurrenceIndex { get; init; }
}
public static class UiToggleStates
{
    public const string Off = "off";
    public const string On = "on";
    public const string Indeterminate = "indeterminate";
    public const string Unknown = "unknown";
}
