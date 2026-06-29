namespace LocalMcp.Contracts.Commands;

public sealed record ClipboardSetCommand : AgentCommand
{
    public required string Text { get; init; }
    public bool Verify { get; init; } = true;
}
