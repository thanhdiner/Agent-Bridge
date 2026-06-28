namespace LocalMcp.Contracts.Commands;
public sealed record WindowMoveCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool RestoreIfNeeded { get; init; } = true;
}