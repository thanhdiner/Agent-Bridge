using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

namespace LocalMcp.Gateway.Security;

public sealed class AgentTokenAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly AgentSecurityOptions _agentSecurityOptions;
    private readonly string? _expectedToken;

    public AgentTokenAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<AgentSecurityOptions> agentSecurityOptions)
        : base(options, logger, encoder)
    {
        _agentSecurityOptions = agentSecurityOptions.Value;

        // Fetch expected token at startup/handler creation
        var envVarName = _agentSecurityOptions.TokenEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(envVarName))
        {
            _expectedToken = Environment.GetEnvironmentVariable(envVarName);
        }

        // If not found in env, check direct config/secrets (fallback for testing)
        if (string.IsNullOrWhiteSpace(_expectedToken))
        {
            // We do not store committed default token, but for test validation it can be set.
        }
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 1. Retrieve the token from Authorization header or access_token query param
        string? token = null;

        var authHeader = Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = authHeader.Substring("Bearer ".Length).Trim();
        }
        else if (Request.Query.TryGetValue("access_token", out var queryToken))
        {
            token = queryToken.ToString();
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(AuthenticateResult.Fail("Agent token is missing."));
        }

        // 2. If authentication is disabled, we bypass (though in practice policy handles this)
        if (!_agentSecurityOptions.AuthenticationEnabled)
        {
            var bypassPrincipal = CreateAgentPrincipal();
            var bypassTicket = new AuthenticationTicket(bypassPrincipal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(bypassTicket));
        }

        // 3. Fail if expected token is missing on Gateway
        if (string.IsNullOrWhiteSpace(_expectedToken))
        {
            Logger.LogError("Agent authentication is enabled but the expected token is missing or not configured.");
            return Task.FromResult(AuthenticateResult.Fail("Gateway is misconfigured: expected Agent token is missing."));
        }

        // 4. Constant-time comparison
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var expectedBytes = Encoding.UTF8.GetBytes(_expectedToken);

        if (FixedTimeEquals(tokenBytes, expectedBytes))
        {
            var principal = CreateAgentPrincipal();
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid Agent token."));
    }

    private ClaimsPrincipal CreateAgentPrincipal()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "AgentDevice"),
            new Claim(ClaimTypes.Role, "Agent")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return new ClaimsPrincipal(identity);
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
