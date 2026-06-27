namespace LocalMcp.Contracts.Commands;

public sealed record ReadRangeCommand : AgentCommand
{
    public required string Path { get; init; }
    public long StartLine { get; init; } = 1;
    public int LineCount { get; init; } = 200;
}
