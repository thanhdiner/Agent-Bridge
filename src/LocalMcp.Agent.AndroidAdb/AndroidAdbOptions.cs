namespace LocalMcp.Agent.AndroidAdb;

public sealed class AndroidAdbOptions
{
    public const string SectionName = "AndroidAdb";

    public string GatewayUrl { get; set; } = "http://127.0.0.1:5227";
    public string AdbPath { get; set; } = "adb";
    public string Serial { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int MaxScreenshotBytes { get; set; } = 6 * 1024 * 1024;
}

public sealed class AgentSecurityOptions
{
    public const string SectionName = "AgentSecurity";

    public bool AuthenticationEnabled { get; set; }
    public string TokenEnvironmentVariable { get; set; } = "LOCALMCP_AGENT_TOKEN";
}
