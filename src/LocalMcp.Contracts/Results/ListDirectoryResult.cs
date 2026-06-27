namespace LocalMcp.Contracts.Results;

public sealed record ListDirectoryResult
{
    public required string NormalizedPath { get; init; }
    public required List<DirectoryEntry> Directories { get; init; }
    public required List<FileEntry> Files { get; init; }
    public required int TotalDirectories { get; init; }
    public required int TotalFiles { get; init; }
}

public sealed record DirectoryEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
}

public sealed record FileEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Extension { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastWriteTimeUtc { get; init; }
}

// Keep FileSystemItemInfo for compatibility in SearchFilesResult if it was used there.
public sealed record FileSystemItemInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset LastModified { get; init; }
}
