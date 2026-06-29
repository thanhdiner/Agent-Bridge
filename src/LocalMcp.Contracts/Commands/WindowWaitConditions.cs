namespace LocalMcp.Contracts.Commands;

public static class WindowWaitConditions
{
    public const string Exists = "exists";
    public const string NotExists = "not-exists";
    public const string Foreground = "foreground";
    public const string TitleEquals = "title-equals";
    public const string TitleContains = "title-contains";

    public static bool TryNormalize(string? value, out string normalized)
    {
        var candidate = value?.Trim().ToLowerInvariant() ?? string.Empty;
        normalized = candidate switch
        {
            Exists or "appears" => Exists,
            NotExists or "disappears" => NotExists,
            Foreground or "focused" => Foreground,
            TitleEquals => TitleEquals,
            TitleContains => TitleContains,
            _ => string.Empty
        };
        return normalized.Length > 0;
    }

    public static bool RequiresExpectedTitle(string condition) =>
        condition is TitleEquals or TitleContains;
}
