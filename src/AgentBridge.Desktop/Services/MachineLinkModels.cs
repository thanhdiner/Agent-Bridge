using System;

namespace AgentBridge.Desktop.Services;

internal sealed record MachineLinkResponse(
    string AccountId,
    string DeviceId,
    string DeviceName,
    string ActivationToken,
    string Plan,
    bool Activated,
    DateTimeOffset ActivatedAt);
