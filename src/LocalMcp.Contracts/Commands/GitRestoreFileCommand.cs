namespace LocalMcp.Contracts.Commands;

public sealed record GitRestoreFileCommand : AgentCommand
{
    public required string Path { get; init; }
    public required string PathSpec { get; init; }
    public string? ExpectedSha256 { get; init; }
}
