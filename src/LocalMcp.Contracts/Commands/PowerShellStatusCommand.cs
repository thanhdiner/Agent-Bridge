namespace LocalMcp.Contracts.Commands;

public sealed record PowerShellStatusCommand : AgentCommand
{
    public required Guid SessionId { get; init; }
    public long OutputOffset { get; init; } = 0;
    public int MaxOutputBytes { get; init; } = 262_144;
}
