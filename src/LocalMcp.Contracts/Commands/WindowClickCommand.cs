namespace LocalMcp.Contracts.Commands;
public static class WindowMouseButtons
{
    public const string Left = "left";
    public const string Right = "right";
    public const string Middle = "middle";
    public static bool IsSupported(string? value) => value is Left or Right or Middle;
}
public sealed record WindowClickCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public string Button { get; init; } = WindowMouseButtons.Left;
    public int ClickCount { get; init; } = 1;
    public int? ExpectedProcessId { get; init; }
    public string? ExpectedWindowTitle { get; init; }
}
