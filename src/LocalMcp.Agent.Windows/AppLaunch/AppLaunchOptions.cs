namespace LocalMcp.Agent.Windows.AppLaunch;

public sealed class AppLaunchOptions
{
    public const string SectionName = "AppLaunch";

    public List<string> AllowedExecutables { get; set; } = new();
}
