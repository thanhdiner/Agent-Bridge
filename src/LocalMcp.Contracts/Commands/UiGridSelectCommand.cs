namespace LocalMcp.Contracts.Commands;

public sealed record UiGridSelectCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public string Action { get; init; } = UiGridSelectActions.Select;
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public int OccurrenceIndex { get; init; }
    public int Row { get; init; }
    public int Column { get; init; }
    public bool FocusWindow { get; init; } = true;
}

public static class UiGridSelectActions
{
    public const string Select = "select";
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Activate = "activate";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is Select or Add or Remove or Activate;
    }

    public static bool? ExpectedSelected(string action) => action switch
    {
        Select or Add => true,
        Remove => false,
        _ => null
    };
}
