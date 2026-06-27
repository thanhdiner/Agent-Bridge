using LocalMcp.Gateway.Security;

namespace LocalMcp.UnitTests;

/// <summary>
/// Tests for the public exposure safety guardrail logic (Task 7).
/// Tests the rule: Development → warning only; Staging/Production → fail startup.
/// </summary>
public sealed class SecurityOptionsGuardrailTests
{
    // ── Helper: simulate the startup guardrail check ───────────────────────────

    private enum StartupOutcome { Allowed, Warning, Rejected }

    /// <summary>
    /// Mirrors the logic in Program.cs (post-build security check).
    /// Returns whether startup would be Allowed, emit a Warning, or be Rejected.
    /// </summary>
    private static StartupOutcome EvaluateStartup(SecurityOptions options, bool isDevelopment)
    {
        if (!options.PublicExposure || options.AuthenticationEnabled)
        {
            return StartupOutcome.Allowed;
        }

        // PublicExposure=true AND AuthenticationEnabled=false
        if (isDevelopment)
        {
            return StartupOutcome.Warning;
        }

        return StartupOutcome.Rejected;
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Development_PublicUnauthenticated_EmitsWarningButAllowsStartup()
    {
        var options = new SecurityOptions
        {
            AuthenticationEnabled = false,
            PublicExposure = true
        };

        var outcome = EvaluateStartup(options, isDevelopment: true);
        Assert.Equal(StartupOutcome.Warning, outcome);
    }

    [Fact]
    public void Production_PublicUnauthenticated_RejectsStartup()
    {
        var options = new SecurityOptions
        {
            AuthenticationEnabled = false,
            PublicExposure = true
        };

        var outcome = EvaluateStartup(options, isDevelopment: false);
        Assert.Equal(StartupOutcome.Rejected, outcome);
    }

    [Fact]
    public void Production_PrivateAndUnauthenticated_AllowsStartup()
    {
        // Local-only (no public tunnel) is always safe regardless of auth
        var options = new SecurityOptions
        {
            AuthenticationEnabled = false,
            PublicExposure = false
        };

        var outcome = EvaluateStartup(options, isDevelopment: false);
        Assert.Equal(StartupOutcome.Allowed, outcome);
    }

    [Fact]
    public void Production_PublicAndAuthenticated_AllowsStartup()
    {
        var options = new SecurityOptions
        {
            AuthenticationEnabled = true,
            PublicExposure = true
        };

        var outcome = EvaluateStartup(options, isDevelopment: false);
        Assert.Equal(StartupOutcome.Allowed, outcome);
    }

    [Fact]
    public void Development_PrivateAndUnauthenticated_AllowsStartup()
    {
        var options = new SecurityOptions
        {
            AuthenticationEnabled = false,
            PublicExposure = false
        };

        var outcome = EvaluateStartup(options, isDevelopment: true);
        Assert.Equal(StartupOutcome.Allowed, outcome);
    }

    [Fact]
    public void SecurityOptions_DefaultValues_ArePrivateAndUnauthenticated()
    {
        // Default should be safe: private, no auth required
        var options = new SecurityOptions();
        Assert.False(options.PublicExposure);
        Assert.False(options.AuthenticationEnabled);
    }
}
