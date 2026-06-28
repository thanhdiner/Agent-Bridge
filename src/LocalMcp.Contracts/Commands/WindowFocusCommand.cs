namespace LocalMcp.Contracts.Commands;
public sealed record WindowFocusCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
}