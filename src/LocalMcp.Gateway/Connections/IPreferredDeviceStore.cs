namespace LocalMcp.Gateway.Connections;

public interface IPreferredDeviceStore
{
    string? GetPreferredDeviceId();
    void SetPreferredDeviceId(string deviceId);
    void ClearPreferredDeviceId();
}
