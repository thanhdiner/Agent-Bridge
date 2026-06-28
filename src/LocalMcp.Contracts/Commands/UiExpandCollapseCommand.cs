namespace LocalMcp.Contracts.Commands;

public sealed record UiExpandCollapseCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public string Action { get; init; } = UiExpandCollapseActions.Toggle;
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public int OccurrenceIndex { get; init; }
    public bool FocusWindow { get; init; } = true;
}

public static class UiExpandCollapseActions
{
    public const string Expand = "expand";
    public const string Collapse = "collapse";
    public const string Toggle = "toggle";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is Expand or Collapse or Toggle;
    }
}
