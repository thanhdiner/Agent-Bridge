using System;

namespace AgentBridge.Desktop.Services;

public enum ManagedServiceState
{
    Stopped,
    Starting,
    Running,
    External,
    Error
}

public sealed record ManagedServiceStatus(
    ManagedServiceState State,
    string Summary,
    string Detail,
    int? ProcessId,
    bool IsHealthy,
    bool IsManaged);

public sealed record SupervisorSnapshot(
    ManagedServiceStatus Gateway,
    ManagedServiceStatus Agent,
    ManagedServiceStatus Tunnel,
    string DeviceId,
    string GatewayUrl,
    string LogsDirectory,
    DateTimeOffset UpdatedAtUtc)
{
    public static SupervisorSnapshot Initial(string gatewayUrl, string logsDirectory) => new(
        new ManagedServiceStatus(
            ManagedServiceState.Stopped,
            "Stopped",
            "Gateway has not started yet.",
            null,
            false,
            false),
        new ManagedServiceStatus(
            ManagedServiceState.Stopped,
            "Stopped",
            "Windows Agent has not started yet.",
            null,
            false,
            false),
        new ManagedServiceStatus(
            ManagedServiceState.Stopped,
            "Stopped",
            "Cloudflare Tunnel has not started yet.",
            null,
            false,
            false),
        "Preparing device identity...",
        gatewayUrl,
        logsDirectory,
        DateTimeOffset.UtcNow);
}

public sealed record LaunchTarget(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    string DisplayPath);
