namespace LocalMcp.Agent.Windows.Connection;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string DeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GatewayUrl { get; set; } = string.Empty;
}
