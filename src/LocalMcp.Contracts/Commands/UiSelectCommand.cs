namespace LocalMcp.Contracts.Commands;

public sealed record UiSelectCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public string Action { get; init; } = UiSelectActions.Select;
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public int OccurrenceIndex { get; init; }
    public bool FocusWindow { get; init; } = true;
}

public static class UiSelectActions
{
    public const string Select = "select";
    public const string Add = "add";
    public const string Remove = "remove";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is Select or Add or Remove;
    }

    public static bool ExpectedSelected(string action) => action != Remove;
}
