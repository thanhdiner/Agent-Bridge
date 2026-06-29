namespace LocalMcp.Contracts.Commands;

public sealed record WindowDragCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public int StartX { get; init; }
    public int StartY { get; init; }
    public int EndX { get; init; }
    public int EndY { get; init; }
    public string Button { get; init; } = WindowMouseButtons.Left;
    public int DurationMs { get; init; } = 300;
    public int Steps { get; init; } = 20;
    public int? ExpectedProcessId { get; init; }
    public string? ExpectedWindowTitle { get; init; }
}
