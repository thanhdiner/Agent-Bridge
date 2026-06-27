namespace LocalMcp.Contracts.Commands;

public sealed record ReadFileCommand : AgentCommand
{
    public required string Path { get; init; }
}
