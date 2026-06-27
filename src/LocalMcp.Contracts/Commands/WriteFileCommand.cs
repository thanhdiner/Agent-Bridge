namespace LocalMcp.Contracts.Commands;

public sealed record WriteFileCommand : AgentCommand
{
    public required string Path { get; init; }
    public required string Content { get; init; }
    public string? ExpectedSha256 { get; init; }
    public bool CreateIfMissing { get; init; } = false;
}
