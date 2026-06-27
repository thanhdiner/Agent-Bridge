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
        options.Audience = _securityOptions.OAuth.Audience;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
        };

        // Custom challenge header builder
        var publicBaseUrl = _securityOptions.PublicBaseUrl;
        var requiredScopes = _securityOptions.OAuth.RequiredScopes;

        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                // Skip default ASP.NET Core challenge response to prevent leaking internal validation exceptions
                context.HandleResponse();

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";

                var metadataUrl = $"{publicBaseUrl.TrimEnd('/')}/.well-known/oauth-protected-resource";
                var scopesStr = string.Join(" ", requiredScopes.Distinct());
                context.Response.Headers.Append("WWW-Authenticate", $"Bearer resource_metadata=\"{metadataUrl}\", scope=\"{scopesStr}\"");

                return Task.CompletedTask;
            }
        };
    }

    public void Configure(JwtBearerOptions options)
    {
        Configure(JwtBearerDefaults.AuthenticationScheme, options);
    }
}
