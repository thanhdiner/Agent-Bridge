namespace LocalMcp.Contracts.Results;

public sealed record UiTextReadResult
{
    public required string WindowHandle { get; init; }
    public int ProcessId { get; init; }
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public int OccurrenceIndex { get; init; }
    public required string Scope { get; init; }
    public string? Text { get; init; }
    public int CharacterCount { get; init; }
    public bool CharacterCountExact { get; init; }
    public int ReturnedCharacters { get; init; }
    public int StartLine { get; init; }
    public int RequestedLineCount { get; init; }
    public int ReturnedLineCount { get; init; }
    public int SelectionCount { get; init; }
    public bool? IsReadOnly { get; init; }
    public bool IsPassword { get; init; }
    public bool Redacted { get; init; }
    public int? CaretPosition { get; init; }
    public bool CaretPositionExact { get; init; }
    public required IReadOnlyList<string> PatternsUsed { get; init; }
    public bool Truncated { get; init; }
}
