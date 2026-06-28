namespace LocalMcp.Contracts.Commands;

public sealed record WindowListCommand : AgentCommand
{
    public bool IncludeInvisible { get; init; }
    public bool IncludeUntitled { get; init; }
    public int MaxWindows { get; init; } = 100;
}
