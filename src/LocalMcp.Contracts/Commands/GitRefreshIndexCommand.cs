namespace LocalMcp.Contracts.Commands;

public sealed record GitRefreshIndexCommand : AgentCommand
{
    public required string Path { get; init; }
    public required string PathSpec { get; init; }
}
