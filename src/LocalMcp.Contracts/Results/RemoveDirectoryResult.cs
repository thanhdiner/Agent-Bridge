namespace LocalMcp.Contracts.Results;

public sealed record RemoveDirectoryResult
{
    public required string Path { get; init; }
    public bool Removed { get; init; }
}
