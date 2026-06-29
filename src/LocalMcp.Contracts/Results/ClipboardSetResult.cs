namespace LocalMcp.Contracts.Results;

public sealed record ClipboardSetResult
{
    public int CharacterCount { get; init; }
    public bool Verified { get; init; }
}
