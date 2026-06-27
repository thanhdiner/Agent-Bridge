using System;

namespace LocalMcp.Contracts.Commands;

public sealed record StatCommand : AgentCommand
{
    public required string Path { get; init; }
}
