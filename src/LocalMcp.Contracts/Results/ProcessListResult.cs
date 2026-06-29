namespace LocalMcp.Contracts.Results;

public sealed record ProcessListResult
{
    public int Count { get; init; }
    public bool Truncated { get; init; }
    public required IReadOnlyList<ProcessListItem> Processes { get; init; }
}

public sealed record ProcessListItem
{
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public int? SessionId { get; init; }
    public string? MainWindowHandle { get; init; }
    public string? MainWindowTitle { get; init; }
    public bool? Responding { get; init; }
    public DateTimeOffset? StartTimeUtc { get; init; }
    public long? WorkingSetBytes { get; init; }
}
