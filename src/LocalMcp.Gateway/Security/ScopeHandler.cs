using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using System.Text.Json;

namespace LocalMcp.Gateway.Security;

public sealed class ScopeHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeRequirement requirement)
    {
        if (context.User == null)
        {
            return Task.CompletedTask;
        }

        // Extract all scopes from the principal's claims
        var userScopes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var claim in context.User.Claims)
        {
            if (string.Equals(claim.Type, "scope", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claim.Type, "scp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claim.Type, "http://schemas.microsoft.com/identity/claims/scope", StringComparison.OrdinalIgnoreCase))
            {
                // The claim value could be space-separated, e.g., "files:read other:scope"
                var split = claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var s in split)
                {
                    userScopes.Add(s);
                }
            }
        }

        // Verify if all required scopes are present
        var hasAll = true;
        foreach (var reqScope in requirement.RequiredScopes)
        {
            if (!userScopes.Contains(reqScope))
            {
                hasAll = false;
                break;
            }
        }

        if (hasAll)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
