namespace LocalMcp.Contracts.Commands;
public sealed record UiRangeValueCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public string Action { get; init; } = UiRangeValueActions.Get;
    public double? Value { get; init; }
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public int OccurrenceIndex { get; init; }
    public bool FocusWindow { get; init; } = true;
}
public static class UiRangeValueActions
{
    public const string Get = "get";
    public const string Set = "set";
    public const string Increase = "increase";
    public const string Decrease = "decrease";
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is Get or Set or Increase or Decrease;
    }
}
