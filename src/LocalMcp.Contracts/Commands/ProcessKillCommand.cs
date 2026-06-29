namespace LocalMcp.Contracts.Commands;

public sealed record ProcessKillCommand : AgentCommand
{
    public int ProcessId { get; init; }
    public string? ExpectedProcessName { get; init; }
    public bool EntireProcessTree { get; init; } = true;
    public int TimeoutMs { get; init; } = 5_000;
}
