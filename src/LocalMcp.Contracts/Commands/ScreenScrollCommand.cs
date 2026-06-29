namespace LocalMcp.Contracts.Commands;

public static class ScreenScrollDirections
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
}

public sealed record ScreenScrollCommand : AgentCommand
{
    public required string ExpectedForegroundWindowHandle { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int? MonitorIndex { get; init; }
    public required string Direction { get; init; }
    public int Notches { get; init; } = 3;
    public int? ExpectedProcessId { get; init; }
    public string? ExpectedWindowTitle { get; init; }
}
