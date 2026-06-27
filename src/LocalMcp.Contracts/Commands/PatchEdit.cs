namespace LocalMcp.Contracts.Commands;

public sealed record PatchEdit
{
    public required string OldText { get; init; }
    public required string NewText { get; init; }
    public bool ReplaceAll { get; init; } = false;
}
