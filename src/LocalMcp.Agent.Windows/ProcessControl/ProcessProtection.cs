namespace LocalMcp.Agent.Windows.ProcessControl;

internal static class ProcessProtection
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "registry",
        "smss",
        "csrss",
        "wininit",
        "services",
        "lsass",
        "winlogon",
        "svchost",
        "fontdrvhost",
        "dwm"
    };

    public static bool IsProtected(int processId, string processName, int currentProcessId) =>
        processId <= 4
        || processId == currentProcessId
        || ProtectedNames.Contains(NormalizeName(processName));

    public static string NormalizeName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return string.Empty;

        var normalized = processName.Trim();
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }
}
