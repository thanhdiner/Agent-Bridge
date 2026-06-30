namespace LocalMcp.Gateway.Licensing;

public interface ILicenseGate
{
    LicenseDecision Evaluate(string deviceId);
}
