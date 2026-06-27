namespace LocalMcp.Contracts.Commands;

public sealed record PowerShellCancelCommand : AgentCommand
{
    public required Guid SessionId { get; init; }
}
