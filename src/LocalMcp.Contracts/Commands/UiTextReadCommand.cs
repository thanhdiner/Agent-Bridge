namespace LocalMcp.Contracts.Commands;

public sealed record UiTextReadCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public string Scope { get; init; } = UiTextReadScopes.Document;
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public int OccurrenceIndex { get; init; }
    public int StartLine { get; init; }
    public int LineCount { get; init; } = 200;
    public int MaxCharacters { get; init; } = 65_536;
    public bool FocusWindow { get; init; }
}

public static class UiTextReadScopes
{
    public const string Document = "document";
    public const string Visible = "visible";
    public const string Selection = "selection";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is Document or Visible or Selection;
    }
}
