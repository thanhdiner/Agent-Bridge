namespace LocalMcp.Contracts.Commands;

public sealed record AppCloseCommand : AgentCommand
{
    public int? ProcessId { get; init; }
    public string? ProcessName { get; init; }
    public bool AllMatches { get; init; }
    public bool Force { get; init; }
    public bool EntireProcessTree { get; init; }
    public int TimeoutMs { get; init; } = 5_000;
}
