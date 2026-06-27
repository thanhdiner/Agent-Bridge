namespace LocalMcp.Contracts.Commands;

public sealed record PowerShellExecuteCommand : AgentCommand
{
    public required string WorkingDirectory { get; init; }
    public required string Script { get; init; }
    public int TimeoutSeconds { get; init; } = 120;
    public int MaxOutputBytes { get; init; } = 1_048_576;
}