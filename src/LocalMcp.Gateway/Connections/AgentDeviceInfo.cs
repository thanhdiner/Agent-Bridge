namespace LocalMcp.Gateway.Connections;

public sealed record AgentDeviceInfo(
    string DeviceId,
    string? DisplayName,
    string ConnectionId,
    DateTimeOffset ConnectedAtUtc,
    string Platform = "windows",
    IReadOnlyList<string>? Capabilities = null);
