using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace LocalMcp.Gateway.Security;

public sealed class ConfigureJwtBearerOptions : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly SecurityOptions _securityOptions;

    public ConfigureJwtBearerOptions(IOptions<SecurityOptions> securityOptions)
    {
        _securityOptions = securityOptions.Value;
    }

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        if (!_securityOptions.AuthenticationEnabled)
        {
            return;
        }

        options.Authority = _securityOptions.OAuth.Authority;
        options.Audience = string.IsNullOrWhiteSpace(_securityOptions.OAuth.Audience) ? null : _securityOptions.OAuth.Audience;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
        };

        // Custom challenge header builder
        var publicBaseUrl = _securityOptions.PublicBaseUrl;
        var requiredScopes = _securityOptions.OAuth.RequiredScopes;

        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                // Skip default ASP.NET Core challenge response
                context.HandleResponse();

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";

                var requestPath = context.Request.Path.Value ?? string.Empty;
                var isB = requestPath.EndsWith("/mcp/b", StringComparison.OrdinalIgnoreCase);
                var metadataSuffix = isB
                    ? "/.well-known/oauth-protected-resource/mcp/b"
                    : "/.well-known/oauth-protected-resource";

                var endpointRealm = isB ? $"{publicBaseUrl.TrimEnd('/')}/mcp/b" : $"{publicBaseUrl.TrimEnd('/')}/mcp/a";
                var metadataUrl = $"{publicBaseUrl.TrimEnd('/')}{metadataSuffix}";
                var scopesStr = string.Join(" ", requiredScopes.Distinct());
                context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
                context.Response.Headers.Append("WWW-Authenticate", $"Bearer realm=\"{endpointRealm}\", resource_metadata=\"{metadataUrl}\", scope=\"{scopesStr}\"");
                await context.Response.WriteAsync("{\"error\":\"unauthorized\",\"message\":\"Authentication required\"}");
            }
        };
    }

    public void Configure(JwtBearerOptions options)
    {
        Configure(JwtBearerDefaults.AuthenticationScheme, options);
    }
}
