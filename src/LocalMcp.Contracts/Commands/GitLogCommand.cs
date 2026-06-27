namespace LocalMcp.Contracts.Commands;

public sealed record GitLogCommand : AgentCommand
{
    public required string Path { get; init; }
    public int MaxCount { get; init; } = 20;
    public int Skip { get; init; }
    public string? PathSpec { get; init; }
    public string? Author { get; init; }
    public string? Since { get; init; }
    public string? Until { get; init; }
    public bool IncludeStats { get; init; }
}
