using System;
using System.Collections.Generic;

namespace AgentBridge.Desktop.Services;

internal sealed record MachineLinkResponse(
    string AccountId,
    string DeviceId,
    string DeviceName,
    string ActivationToken,
    bool Activated,
    string? Status,
    DateTimeOffset? ActiveUntilUtc,
    IReadOnlyList<string>? Features,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
