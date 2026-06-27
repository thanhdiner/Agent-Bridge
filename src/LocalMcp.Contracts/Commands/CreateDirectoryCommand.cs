using System;

namespace LocalMcp.Contracts.Commands;

public sealed record CreateDirectoryCommand : AgentCommand
{
    public required string Path { get; init; }
    public bool Recursive { get; init; }
}
