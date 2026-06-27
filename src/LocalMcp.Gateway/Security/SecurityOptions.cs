namespace LocalMcp.Gateway.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public bool AuthenticationEnabled { get; set; } = false;
    public bool PublicExposure { get; set; } = false;
    public string PublicBaseUrl { get; set; } = string.Empty;
    public OAuthOptions OAuth { get; set; } = new();
}

public sealed class OAuthOptions
{
    public string Authority { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public List<string> RequiredScopes { get; set; } = new() { "files:read" };
}
