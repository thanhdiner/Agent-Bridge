namespace LocalMcp.Contracts.Commands;
public sealed record UiToggleCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public string Action { get; init; } = UiToggleActions.Toggle;
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public int OccurrenceIndex { get; init; }
    public bool FocusWindow { get; init; } = true;
}
public static class UiToggleActions
{
    public const string On = "on";
    public const string Off = "off";
    public const string Toggle = "toggle";
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is On or Off or Toggle;
    }
}
