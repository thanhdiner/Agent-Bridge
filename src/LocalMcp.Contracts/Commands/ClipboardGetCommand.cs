namespace LocalMcp.Contracts.Commands;

public sealed record ClipboardGetCommand : AgentCommand
{
    public int MaxCharacters { get; init; } = 65_536;
}
