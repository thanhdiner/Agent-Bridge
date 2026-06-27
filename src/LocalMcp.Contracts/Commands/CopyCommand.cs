using System;

namespace LocalMcp.Contracts.Commands;

public sealed record CopyCommand : AgentCommand
{
    public required string Path { get; init; }
    public required string Destination { get; init; }
    public bool Overwrite { get; init; } = false;
    public string? ExpectedSourceSha256 { get; init; }
    public bool Recursive { get; init; } = false;
    public int MaxEntries { get; init; } = 1000;
    public long MaxTotalBytes { get; init; } = 104857600;
}
