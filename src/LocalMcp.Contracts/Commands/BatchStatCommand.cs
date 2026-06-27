namespace LocalMcp.Contracts.Commands;

public sealed record BatchStatCommand : AgentCommand
{
    public required List<string> Paths { get; init; }
}
