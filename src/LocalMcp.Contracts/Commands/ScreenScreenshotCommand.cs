namespace LocalMcp.Contracts.Commands;

public sealed record ScreenScreenshotCommand : AgentCommand
{
    public int? MonitorIndex { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int MaxWidth { get; init; } = 4096;
    public int MaxHeight { get; init; } = 4096;
}
