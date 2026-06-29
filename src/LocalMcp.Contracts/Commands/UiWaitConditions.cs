namespace LocalMcp.Contracts.Commands;

public static class UiWaitConditions
{
    public const string Exists = "exists";
    public const string NotExists = "not-exists";
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
    public const string Focused = "focused";
    public const string ValueEquals = "value-equals";
    public const string ValueContains = "value-contains";
    public const string ValueChanged = "value-changed";

    public static bool TryNormalize(string? value, out string normalized)
    {
        var candidate = value?.Trim().ToLowerInvariant() ?? string.Empty;
        normalized = candidate switch
        {
            Exists or "appears" => Exists,
            NotExists or "disappears" => NotExists,
            Enabled => Enabled,
            Disabled => Disabled,
            Focused => Focused,
            ValueEquals => ValueEquals,
            ValueContains => ValueContains,
            ValueChanged or "value_changed" => ValueChanged,
            _ => string.Empty
        };
        return normalized.Length > 0;
    }

    public static bool RequiresExpectedValue(string condition) =>
        condition is ValueEquals or ValueContains;
}
