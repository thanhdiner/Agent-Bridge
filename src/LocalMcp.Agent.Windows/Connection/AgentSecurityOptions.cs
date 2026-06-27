namespace LocalMcp.Agent.Windows.Connection;

public sealed class AgentSecurityOptions
{
    public const string SectionName = "AgentSecurity";

    public bool AuthenticationEnabled { get; set; } = false;
    public string TokenEnvironmentVariable { get; set; } = "LOCALMCP_AGENT_TOKEN";
}
