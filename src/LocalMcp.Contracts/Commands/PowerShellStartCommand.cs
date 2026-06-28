namespace LocalMcp.Contracts.Commands;

public sealed record PowerShellStartCommand : AgentCommand
{
    public required string WorkingDirectory { get; init; }
    public required string Script { get; init; }
    public bool Visible { get; init; }
    public bool Elevated { get; init; }
    public int TimeoutSeconds { get; init; } = 900;
    public int MaxOutputBytes { get; init; } = 1_048_576;
}
