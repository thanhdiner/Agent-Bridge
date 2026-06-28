namespace LocalMcp.Contracts.Commands;

public sealed record AppLaunchCommand : AgentCommand
{
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public bool WaitForWindow { get; init; } = true;
    public string? WindowTitleContains { get; init; }
    public int TimeoutMs { get; init; } = 15_000;
    public int PollIntervalMs { get; init; } = 100;
}
