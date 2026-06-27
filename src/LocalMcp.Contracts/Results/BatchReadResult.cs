namespace LocalMcp.Contracts.Results;

public sealed record BatchReadFileResult
{
    public required string Path { get; init; }
    public required string Content { get; init; }
    public required string Encoding { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
    public required int BytesReturned { get; init; }
    public required bool Truncated { get; init; }
}

public sealed record BatchReadItemResult
{
    public required string Path { get; init; }
    public bool Success { get; init; }
    public BatchReadFileResult? Data { get; init; }
    public CommandError? Error { get; init; }
}

public sealed record BatchReadResult
{
    public required IReadOnlyList<BatchReadItemResult> Items { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public long TotalBytesReturned { get; init; }
}
