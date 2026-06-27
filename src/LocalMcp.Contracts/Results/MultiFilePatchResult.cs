namespace LocalMcp.Contracts.Results;

public sealed record MultiFilePatchItemResult
{
    public required string Path { get; init; }
    public bool Success { get; init; }
    public PatchFileResult? Data { get; init; }
    public CommandError? Error { get; init; }
}

public sealed record MultiFilePatchResult
{
    public required IReadOnlyList<MultiFilePatchItemResult> Items { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
}
