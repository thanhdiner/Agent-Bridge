namespace LocalMcp.Gateway.Connections;

public interface IAgentConnectionRegistry
{
    void Register(string deviceId, string connectionId);
    void Unregister(string connectionId);
    string? GetConnectionId(string deviceId);
    string? GetDeviceId(string connectionId);
    IReadOnlyCollection<string> GetActiveDevices();
}
