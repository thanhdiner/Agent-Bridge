namespace LocalMcp.Contracts.Results;

public sealed record ClipboardGetResult
{
    public bool HasText { get; init; }
    public string? Text { get; init; }
    public int CharacterCount { get; init; }
    public bool CharacterCountExact { get; init; }
    public int ReturnedCharacters { get; init; }
    public bool Truncated { get; init; }
}
