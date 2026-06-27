namespace LocalMcp.Contracts.Commands;

public sealed record DeleteCommand : AgentCommand
{
    public required string Path { get; init; }
    public string? ExpectedSha256 { get; init; }
    public bool MissingOk { get; init; } = false;
}
