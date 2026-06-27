namespace LocalMcp.Contracts.Commands;

public sealed record ProjectCheckCommand : AgentCommand
{
    public required string Path { get; init; }
    public string ProjectType { get; init; } = "auto";
    public List<string> Steps { get; init; } = ["build", "test"];
    public string Configuration { get; init; } = "Debug";
    public int TimeoutSeconds { get; init; } = 300;
    public int MaxOutputBytes { get; init; } = 1_048_576;
}
