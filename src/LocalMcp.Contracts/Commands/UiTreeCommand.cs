namespace LocalMcp.Contracts.Commands;

public sealed record UiTreeCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public int MaxDepth { get; init; } = 6;
    public int MaxNodes { get; init; } = 500;
}
