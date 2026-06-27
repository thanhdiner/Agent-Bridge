namespace LocalMcp.Contracts.Results;

public sealed record BatchStatItemResult
{
    public required string Path { get; init; }
    public bool Success { get; init; }
    public StatResult? Data { get; init; }
    public CommandError? Error { get; init; }
}

public sealed record BatchStatResult
{
    public required IReadOnlyList<BatchStatItemResult> Items { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
}
