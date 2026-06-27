namespace LocalMcp.Contracts.Commands;

public sealed record RemoveDirectoryCommand : AgentCommand
{
    public required string Path { get; init; }
    public bool MissingOk { get; init; } = false;
}
