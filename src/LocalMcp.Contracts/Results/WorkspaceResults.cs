namespace LocalMcp.Contracts.Results;

public sealed record WorkspaceInfo
{
    public required string Alias { get; init; }
    public required string RootPath { get; init; }
    public string? Description { get; init; }
    public required bool Available { get; init; }
    public required bool Allowed { get; init; }
    public required bool Writable { get; init; }
}

public sealed record WorkspaceListResult
{
    public IReadOnlyList<WorkspaceInfo> Workspaces { get; init; } = [];
}

public sealed record WorkspaceResolveResult
{
    public required string Alias { get; init; }
    public required string RootPath { get; init; }
    public required string RelativePath { get; init; }
    public required string AbsolutePath { get; init; }
    public required bool Writable { get; init; }
    public required bool Exists { get; init; }
    public required string EntryType { get; init; }
}
