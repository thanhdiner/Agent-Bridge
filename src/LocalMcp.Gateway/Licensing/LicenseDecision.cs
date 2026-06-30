namespace LocalMcp.Gateway.Licensing;

public sealed record LicenseDecision(bool Allowed, string? ErrorCode = null, string? Reason = null)
{
    public static LicenseDecision Allow() => new(true);
    public static LicenseDecision Deny(string errorCode, string reason) => new(false, errorCode, reason);
}
