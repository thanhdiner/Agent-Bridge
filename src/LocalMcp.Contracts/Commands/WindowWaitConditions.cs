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
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is Exists
            or NotExists
            or Foreground
            or TitleEquals
            or TitleContains;
    }

    public static bool RequiresExpectedTitle(string condition) =>
        condition is TitleEquals or TitleContains;
}
