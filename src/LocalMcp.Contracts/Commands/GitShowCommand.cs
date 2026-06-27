namespace LocalMcp.Contracts.Commands;

public sealed record GitShowCommand : AgentCommand
{
    public required string Path { get; init; }
    public required string Revision { get; init; }
    public List<string> PathSpecs { get; init; } = [];
    public bool IncludePatch { get; init; } = true;
    public bool IncludeStats { get; init; } = true;
    public int ContextLines { get; init; } = 3;
    public int MaxBytes { get; init; } = 1_048_576;
}
