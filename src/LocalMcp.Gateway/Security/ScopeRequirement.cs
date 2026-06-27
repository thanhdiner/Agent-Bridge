using Microsoft.AspNetCore.Authorization;

namespace LocalMcp.Gateway.Security;

public sealed class ScopeRequirement : IAuthorizationRequirement
{
    public IReadOnlyList<string> RequiredScopes { get; }

    public ScopeRequirement(IEnumerable<string> requiredScopes)
    {
        RequiredScopes = requiredScopes?.ToList() ?? new List<string>();
    }
}
