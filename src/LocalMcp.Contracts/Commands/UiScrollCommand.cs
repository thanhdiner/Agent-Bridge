namespace LocalMcp.Contracts.Commands;
public sealed record UiScrollCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public required string Direction { get; init; }
    public string Amount { get; init; } = UiScrollAmounts.Page;
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public int OccurrenceIndex { get; init; }
    public bool FocusWindow { get; init; } = true;
}
public static class UiScrollDirections
{
    public const string Up = "up";
    public const string Down = "down";
    public const string Left = "left";
    public const string Right = "right";
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is Up or Down or Left or Right;
    }
    public static bool IsVertical(string direction) => direction is Up or Down;
}
public static class UiScrollAmounts
{
    public const string Small = "small";
    public const string Page = "page";
    public const string End = "end";
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is Small or Page or End;
    }
}
