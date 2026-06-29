namespace LocalMcp.Contracts.Commands;

public sealed record ProcessListCommand : AgentCommand
{
    public string? NameContains { get; init; }
    public bool IncludeWindowless { get; init; } = true;
    public int MaxResults { get; init; } = 200;
}
