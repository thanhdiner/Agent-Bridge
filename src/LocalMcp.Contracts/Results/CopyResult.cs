using System;

namespace LocalMcp.Contracts.Results;

public sealed record CopyResult
{
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public int FilesCopied { get; init; }
    public int DirectoriesCreated { get; init; }
    public required long BytesCopied { get; init; }
    public string? Sha256 { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }
}
