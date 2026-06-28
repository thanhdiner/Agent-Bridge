namespace LocalMcp.Contracts.Commands;

public static class UiWaitConditions
{
    public const string Exists = "exists";
    public const string NotExists = "not-exists";
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
    public const string ValueEquals = "value-equals";
    public const string ValueContains = "value-contains";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is Exists
            or NotExists
            or Enabled
            or Disabled
            or ValueEquals
            or ValueContains;
    }

    public static bool RequiresExpectedValue(string condition) =>
        condition is ValueEquals or ValueContains;
}
