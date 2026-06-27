namespace LocalMcp.Contracts.Commands;

public sealed record MultiFilePatchItem
{
    public required string Path { get; init; }
    public required string ExpectedSha256 { get; init; }
    public required List<PatchEdit> Edits { get; init; }
}

public sealed record MultiFilePatchCommand : AgentCommand
{
    public required List<MultiFilePatchItem> Items { get; init; }
}
