namespace LocalMcp.Agent.Windows.AppLaunch;

internal static class AppOpenAliasCatalog
{
    private static readonly IReadOnlyDictionary<string, AppOpenAlias> Aliases =
        new Dictionary<string, AppOpenAlias>(StringComparer.OrdinalIgnoreCase)
        {
            ["youtube"] = new("chrome", ["https://www.youtube.com"], "YouTube"),
            ["yt"] = new("chrome", ["https://www.youtube.com"], "YouTube")
        };

    public static AppOpenTarget Resolve(string appId, IReadOnlyList<string> arguments)
    {
        if (!AppResolver.TryNormalizeAppId(appId, out var normalizedAppId)
            || !Aliases.TryGetValue(normalizedAppId, out var alias))
        {
            return new AppOpenTarget(
                appId,
                arguments,
                AliasApplied: false,
                DefaultWindowTitleContains: null);
        }

        var mergedArguments = alias.Arguments.Count == 0
            ? arguments
            : alias.Arguments.Concat(arguments).ToArray();

        return new AppOpenTarget(
            alias.AppId,
            mergedArguments,
            AliasApplied: true,
            DefaultWindowTitleContains: alias.DefaultWindowTitleContains);
    }

    private sealed record AppOpenAlias(
        string AppId,
        IReadOnlyList<string> Arguments,
        string? DefaultWindowTitleContains);
}

internal sealed record AppOpenTarget(
    string AppId,
    IReadOnlyList<string> Arguments,
    bool AliasApplied,
    string? DefaultWindowTitleContains);
