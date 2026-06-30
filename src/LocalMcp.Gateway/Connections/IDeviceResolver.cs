namespace LocalMcp.Gateway.Connections;

public interface IDeviceResolver
{
    DeviceResolution Resolve(string? requestedDeviceId);
}

public sealed record DeviceResolution(
    bool Success,
    string? DeviceId,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static DeviceResolution Resolved(string deviceId) =>
        new(true, deviceId, null, null);

    public static DeviceResolution Failed(string errorCode, string errorMessage) =>
        new(false, null, errorCode, errorMessage);
}
