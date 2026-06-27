using System;

namespace LocalMcp.Contracts.Results;

public sealed record CopyResult
{
    public required string Path { get; init; }
    public required long BytesCopied { get; init; }
    public required string Sha256 { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }
}
