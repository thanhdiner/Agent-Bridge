namespace LocalMcp.Contracts.Commands;

public sealed record WorkspaceResolveCommand : AgentCommand
{
    public required string Alias { get; init; }
    public string? RelativePath { get; init; }
    public bool RequireWritable { get; init; }
}
