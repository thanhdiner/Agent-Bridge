namespace LocalMcp.Contracts.Commands;
public sealed record WindowCloseCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
}