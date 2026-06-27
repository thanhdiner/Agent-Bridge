namespace LocalMcp.Agent.Windows.Security;

public sealed class FileAccessOptions
{
    public const string SectionName = "FileAccess";

    public List<string> AllowedRoots { get; set; } = new();
    public List<string> WritableRoots { get; set; } = new();
    public List<string> DeniedSegments { get; set; } = new();
    public List<string> DeniedFileNames { get; set; } = new();
    public List<string> DeniedWriteFileNames { get; set; } = new() { ".env", "id_rsa", "id_ed25519" };
    public List<string> DeniedWriteExtensions { get; set; } = new() { ".pem", ".key", ".pfx", ".p12" };
    public long MaxReadBytes { get; set; } = 2097152; // Default to 2MB (2 * 1024 * 1024 bytes)
    public long MaxWriteBytes { get; set; } = 1048576; // Default to 1MB (1048576 bytes)
}
