using System;

namespace LocalMcp.Contracts.Commands;

public sealed record MoveCommand : AgentCommand
{
    public required string Path { get; init; }
    public required string Destination { get; init; }
    public bool Overwrite { get; init; } = false;
    public string? ExpectedSha256 { get; init; }
}
