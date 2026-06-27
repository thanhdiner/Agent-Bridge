namespace LocalMcp.Contracts.Results;

public sealed record CommandError(
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]>? Details = null
);
