namespace LocalMcp.Contracts.Commands;

public sealed record UiFindCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public string? AutomationId { get; init; }
    public string? NameContains { get; init; }
    public string? ControlType { get; init; }
    public int MaxDepth { get; init; } = 8;
    public int MaxResults { get; init; } = 50;
}
