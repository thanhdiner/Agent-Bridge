namespace LocalMcp.Contracts.Commands;

public static class ProcessWaitConditions
{
    public const string Exists = "exists";
    public const string NotExists = "not-exists";

    public static bool TryNormalize(string? value, out string normalized)
    {
        var candidate = value?.Trim().ToLowerInvariant() ?? string.Empty;
        normalized = candidate switch
        {
            Exists or "appears" => Exists,
            NotExists or "disappears" or "exited" => NotExists,
            _ => string.Empty
        };
        return normalized.Length > 0;
    }
}
