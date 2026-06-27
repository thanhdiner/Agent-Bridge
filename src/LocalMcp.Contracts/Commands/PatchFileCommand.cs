namespace LocalMcp.Contracts.Commands;

public sealed record PatchFileCommand : AgentCommand
{
    public required string Path { get; init; }
    public required string ExpectedSha256 { get; init; }
    public required List<PatchEdit> Edits { get; init; }
}
