namespace LocalMcp.Contracts.Commands;

public sealed record AppResolveCommand : AgentCommand
{
    public required string AppId { get; init; }
    public bool Refresh { get; init; }
}
