using System;

namespace LocalMcp.Contracts.Results;

public sealed record MoveResult
{
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }
}
