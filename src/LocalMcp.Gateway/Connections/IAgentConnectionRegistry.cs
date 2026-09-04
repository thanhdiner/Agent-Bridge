namespace LocalMcp.Gateway.Connections;

public interface IAgentConnectionRegistry
{
    void Register(
        string deviceId,
        string connectionId,
        string? displayName = null,
        string? platform = null,
        IReadOnlyCollection<string>? capabilities = null);
    void Unregister(string connectionId);
    string? GetConnectionId(string deviceId);
    string? GetDeviceId(string connectionId);
    AgentDeviceInfo? GetDevice(string deviceId);
    IReadOnlyCollection<string> GetActiveDevices();
    IReadOnlyCollection<AgentDeviceInfo> GetActiveDeviceInfos();
}
