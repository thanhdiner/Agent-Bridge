namespace LocalMcp.Agent.Windows.Security;

public sealed class FileAccessOptions
{
    public const string SectionName = "FileAccess";

    public List<string> AllowedRoots { get; set; } = new();
    public List<string> DeniedSegments { get; set; } = new();
    public List<string> DeniedFileNames { get; set; } = new();
    public long MaxReadBytes { get; set; } = 2097152; // Default to 2MB (2 * 1024 * 1024 bytes)
}
