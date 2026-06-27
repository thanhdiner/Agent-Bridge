namespace LocalMcp.Contracts.Results;

public sealed record CommandResult<T>
{
    public required Guid CommandId { get; init; }
    public required bool Success { get; init; }
    public T? Data { get; init; }
    public CommandError? Error { get; init; }
}
