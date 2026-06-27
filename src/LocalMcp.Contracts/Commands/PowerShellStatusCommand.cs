namespace LocalMcp.Contracts.Commands;

public sealed record PowerShellStatusCommand : AgentCommand
{
    public required Guid SessionId { get; init; }
    public long StdoutOffset { get; init; } = 0;
    public long StderrOffset { get; init; } = 0;
    public int MaxOutputBytes { get; init; } = 262_144;
}
