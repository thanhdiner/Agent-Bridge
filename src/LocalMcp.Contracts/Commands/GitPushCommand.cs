namespace LocalMcp.Contracts.Commands;

public sealed record GitPushCommand : AgentCommand
{
    public required string Path { get; init; }
    public string? Remote { get; init; }
    public string? Branch { get; init; }
    public bool SetUpstream { get; init; }
    public int TimeoutSeconds { get; init; } = 120;
    public int MaxOutputBytes { get; init; } = 65_536;
}
