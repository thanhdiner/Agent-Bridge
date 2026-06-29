namespace LocalMcp.Contracts.Commands;

public sealed record WindowScreenshotCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public int MaxWidth { get; init; } = 1920;
    public int MaxHeight { get; init; } = 1080;
}
