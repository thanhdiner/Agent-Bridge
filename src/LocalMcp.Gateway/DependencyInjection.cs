using LocalMcp.Gateway.Connections;
using LocalMcp.Gateway;
using LocalMcp.Gateway.Commands;
using LocalMcp.Gateway.Security;
using LocalMcp.Gateway.Licensing;
using LocalMcp.Gateway.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using System.Security.Claims;

namespace Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    public static IServiceCollection AddGatewayServices(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<IAgentConnectionRegistry, InMemoryAgentConnectionRegistry>();
        services.AddSingleton<IPreferredDeviceStore, FilePreferredDeviceStore>();
        services.AddSingleton<IDeviceResolver, DefaultDeviceResolver>();
        services.AddSingleton<DeviceActivationStore>();
        services.AddSingleton<IDeviceActivationStore>(sp => sp.GetRequiredService<DeviceActivationStore>());
        services.AddSingleton<ILicenseGate, LicenseGate>();
        services.AddSingleton<ICommandDispatcher, SignalRCommandDispatcher>();
        services.AddSingleton<IExternalMcpRouter, ExternalMcpRouter>();
        services.AddSingleton<ExternalMcpCatalogCache>();
        services.AddSingleton<ToolVisibilityStore>();
        services.AddSingleton<LocalToolPrimitiveCache>();
        services.AddHostedService<ExternalMcpCatalogWarmupService>();

        var config = configuration ?? new ConfigurationBuilder().Build();

        // 1. Bind options
        var securitySection = config.GetSection(SecurityOptions.SectionName);
        var agentSecuritySection = config.GetSection(AgentSecurityOptions.SectionName);
        var externalMcpSection = config.GetSection(ExternalMcpOptions.SectionName);

        services.AddOptions<ExternalMcpOptions>()
            .Bind(externalMcpSection)
            .PostConfigure(options =>
            {
                if (!options.Servers.ContainsKey("chrome-devtools"))
                {
                    options.Servers["chrome-devtools"] = new ExternalMcpServerOptions
                    {
                        Command = "cmd",
                        Args = ["/c", "npx", "-y", "chrome-devtools-mcp@latest", "--autoConnect"],
                        InitializeTimeoutSeconds = 60,
                        ToolCallTimeoutSeconds = 120
                    };
                }

                if (!options.Servers.ContainsKey("playwright"))
                {
                    options.Servers["playwright"] = new ExternalMcpServerOptions
                    {
                        Command = "cmd",
                        Args = ["/c", "npx", "-y", "@playwright/mcp@latest"],
                        InitializeTimeoutSeconds = 90,
                        ToolCallTimeoutSeconds = 180
                    };
                }

                if (!options.Servers.ContainsKey("puppeteer"))
                {
                    options.Servers["puppeteer"] = new ExternalMcpServerOptions
                    {
                        Command = "cmd",
                        Args = ["/c", "npx", "-y", "@modelcontextprotocol/server-puppeteer"],
                        InitializeTimeoutSeconds = 90,
                        ToolCallTimeoutSeconds = 180
                    };
                }

                if (!options.Servers.ContainsKey("git-mcp"))
                {
                    options.Servers["git-mcp"] = new ExternalMcpServerOptions
                    {
                        Command = "cmd",
                        Args = ["/c", "uvx", "mcp-server-git", "--repository", @"F:\All Project\_Đang build\AgentBridge-Commercial"],
                        InitializeTimeoutSeconds = 60,
                        ToolCallTimeoutSeconds = 120
                    };
                }

                if (!options.Servers.ContainsKey("github-mcp"))
                {
                    options.Servers["github-mcp"] = new ExternalMcpServerOptions
                    {
                        Command = "cmd",
                        Args = ["/c", "npx", "-y", "@modelcontextprotocol/server-github"],
                        InitializeTimeoutSeconds = 60,
                        ToolCallTimeoutSeconds = 120
                    };
                }

                if (!options.Servers.ContainsKey("context7"))
                {
                    options.Servers["context7"] = new ExternalMcpServerOptions
                    {
                        Command = "cmd",
                        Args = ["/c", "npx", "-y", "@upstash/context7-mcp"],
                        InitializeTimeoutSeconds = 60,
                        ToolCallTimeoutSeconds = 120
                    };
                }

                if (!options.Servers.ContainsKey("memory"))
                {
                    options.Servers["memory"] = new ExternalMcpServerOptions
                    {
                        Command = "cmd",
                        Args = ["/c", "npx", "-y", "@modelcontextprotocol/server-memory"],
                        WorkingDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridge", "mcp-memory"),
                        InitializeTimeoutSeconds = 60,
                        ToolCallTimeoutSeconds = 120
                    };
                }

                if (!options.Servers.ContainsKey("sequential-thinking"))
                {
                    options.Servers["sequential-thinking"] = new ExternalMcpServerOptions
                    {
                        Command = "cmd",
                        Args = ["/c", "npx", "-y", "@modelcontextprotocol/server-sequential-thinking"],
                        InitializeTimeoutSeconds = 60,
                        ToolCallTimeoutSeconds = 120
                    };
                }

                if (!options.Servers.ContainsKey("fetch-mcp"))
                {
                    options.Servers["fetch-mcp"] = new ExternalMcpServerOptions
                    {
                        Command = "cmd",
                        Args = ["/c", "uvx", "mcp-server-fetch"],
                        InitializeTimeoutSeconds = 60,
                        ToolCallTimeoutSeconds = 120
                    };
                }
            });

        services.AddOptions<SecurityOptions>()
            .Bind(securitySection)
            .Validate(o =>
            {
                if (!o.AuthenticationEnabled)
                {
                    return true;
                }

                if (string.IsNullOrWhiteSpace(o.PublicBaseUrl))
                {
                    return false;
                }

                if (!Uri.TryCreate(o.PublicBaseUrl, UriKind.Absolute, out var uri))
                {
                    return false;
                }

                var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                var isDev = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase);
                if (!isDev && uri.Scheme != Uri.UriSchemeHttps)
                {
                    return false;
                }

                if (o.OAuth == null)
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(o.OAuth.Authority) || !Uri.TryCreate(o.OAuth.Authority, UriKind.Absolute, out _))
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(o.OAuth.Audience))
                {
                    return false;
                }

                return true;
            }, "Security options are invalid when AuthenticationEnabled is true. PublicBaseUrl must be a valid HTTPS URL (HTTPS required outside Development), and Authority/Audience must be configured.")
            .ValidateOnStart();

        services.AddOptions<AgentSecurityOptions>()
            .Bind(agentSecuritySection)
            .Validate(o =>
            {
                if (!o.AuthenticationEnabled)
                {
                    return true;
                }

                if (string.IsNullOrWhiteSpace(o.TokenEnvironmentVariable))
                {
                    return false;
                }

                var token = Environment.GetEnvironmentVariable(o.TokenEnvironmentVariable);
                if (string.IsNullOrWhiteSpace(token))
                {
                    return false;
                }

                return true;
            }, "AgentSecurity:AuthenticationEnabled is true but the expected token is missing in environment variables.")
            .ValidateOnStart();

        // 2. Normalise PublicBaseUrl
        services.PostConfigure<SecurityOptions>(o =>
        {
            if (!string.IsNullOrEmpty(o.PublicBaseUrl))
            {
                o.PublicBaseUrl = o.PublicBaseUrl.TrimEnd('/');
            }
        });

        // 3. Register authentication
        var authEnabled = securitySection.GetValue<bool>("AuthenticationEnabled");

        var authBuilder = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        });

        if (authEnabled)
        {
            // Real JwtBearer
            services.ConfigureOptions<ConfigureJwtBearerOptions>();
            authBuilder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, _ => { });
        }
        else
        {
            // Local bypass handler when authentication is disabled
            authBuilder.AddScheme<AuthenticationSchemeOptions, DevBypassAuthHandler>(JwtBearerDefaults.AuthenticationScheme, null);
        }

        // Always add AgentToken scheme
        authBuilder.AddScheme<AuthenticationSchemeOptions, AgentTokenAuthHandler>("AgentToken", null);

        // 4. Configure policies and handlers
        services.AddSingleton<IAuthorizationHandler, ScopeHandler>();

        services.AddAuthorization(options =>
        {
            options.AddPolicy("McpAuthenticatedPolicy", policy =>
            {
                if (authEnabled)
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                }
                else
                {
                    policy.RequireAssertion(_ => true); // Allow anonymous
                }
            });

            options.AddPolicy("FilesReadPolicy", policy =>
            {
                if (authEnabled)
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                    policy.Requirements.Add(new ScopeRequirement(new List<string> { "files:read" }));
                }
                else
                {
                    policy.RequireAssertion(_ => true); // Allow anonymous
                }
            });

            options.AddPolicy("FilesWritePolicy", policy =>
            {
                if (authEnabled)
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                    policy.Requirements.Add(new ScopeRequirement(new List<string> { "files:write" }));
                }
                else
                {
                    policy.RequireAssertion(_ => true); // Allow anonymous
                }
            });

            options.AddPolicy("DevExecutePolicy", policy =>
            {
                if (authEnabled)
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                    policy.Requirements.Add(new ScopeRequirement(new List<string> { "dev:execute" }));
                }
                else
                {
                    policy.RequireAssertion(_ => false); // Execution is never anonymous
                }
            });

            var agentAuthEnabled = agentSecuritySection.GetValue<bool>("AuthenticationEnabled");
            options.AddPolicy("AgentPolicy", policy =>
            {
                if (agentAuthEnabled)
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddAuthenticationSchemes("AgentToken");
                }
                else
                {
                    policy.RequireAssertion(_ => true); // Allow anonymous
                }
            });
        });

        return services;
    }
}

/// <summary>
/// A simple local bypass authentication handler used in local Development mode
/// when AuthenticationEnabled is set to false.
/// </summary>
internal sealed class DevBypassAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public DevBypassAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "DevUser"),
            new Claim("scope", "files:read files:write")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}



