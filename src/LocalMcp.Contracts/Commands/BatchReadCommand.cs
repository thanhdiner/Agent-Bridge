namespace LocalMcp.Contracts.Commands;

public sealed record BatchReadCommand : AgentCommand
{
    public required List<string> Paths { get; init; }
    public int MaxBytesPerFile { get; init; } = 262_144;
    public long MaxTotalBytes { get; init; } = 2_097_152;
}
