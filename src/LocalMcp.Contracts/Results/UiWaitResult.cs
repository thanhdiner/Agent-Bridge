namespace LocalMcp.Contracts.Results;

public sealed record UiWaitResult
{
    public required string WindowHandle { get; init; }
    public required string Condition { get; init; }
    public string? ExpectedValue { get; init; }
    public int WaitedMs { get; init; }
    public int PollCount { get; init; }
    public int OccurrenceIndex { get; init; }
    public bool ElementFound { get; init; }
    public string? Name { get; init; }
    public string? AutomationId { get; init; }
    public string? ControlType { get; init; }
    public UiBounds? Bounds { get; init; }
    public bool? Enabled { get; init; }
    public bool IsPassword { get; init; }
    public bool ValueSupported { get; init; }
    public string? Value { get; init; }
    public bool ValueTruncated { get; init; }
}
