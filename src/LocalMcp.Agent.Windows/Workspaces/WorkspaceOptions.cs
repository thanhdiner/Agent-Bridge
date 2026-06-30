namespace LocalMcp.Agent.Windows.Workspaces;

public sealed class WorkspaceOptions
{
    public const string SectionName = "Workspaces";

    public Dictionary<string, WorkspaceDefinition> Aliases { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsValidAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias) || alias.Length > 64)
            return false;

        if (!char.IsLetterOrDigit(alias[0]))
            return false;

        return alias.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
    }
}

public sealed class WorkspaceDefinition
{
    public string Path { get; set; } = string.Empty;
    public bool Writable { get; set; }
    public string? Description { get; set; }
}
