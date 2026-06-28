namespace LocalMcp.Contracts.Results;

public sealed record UiSetValueResult
{
    public required string WindowHandle { get; init; }
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public required string WriteMethod { get; init; }
    public bool IsPassword { get; init; }
    public bool Appended { get; init; }
    public bool Verified { get; init; }
    public int ValueLength { get; init; }
    public string? Value { get; init; }
    public bool ValueTruncated { get; init; }
    public int OccurrenceIndex { get; init; }
}
