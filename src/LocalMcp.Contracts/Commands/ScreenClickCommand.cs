namespace LocalMcp.Contracts.Commands;

public sealed record ScreenClickCommand : AgentCommand
{
    public required string ExpectedForegroundWindowHandle { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int? MonitorIndex { get; init; }
    public string Button { get; init; } = WindowMouseButtons.Left;
    public int ClickCount { get; init; } = 1;
    public int? ExpectedProcessId { get; init; }
    public string? ExpectedWindowTitle { get; init; }
}
