using LocalMcp.Agent.Windows.Connection;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.Commands;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentOptions>()
            .Bind(configuration.GetSection(AgentOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.DeviceId), "Agent:DeviceId is required.")
            .Validate(o => Uri.TryCreate(o.GatewayUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps), "Agent:GatewayUrl must be a valid HTTP/HTTPS URL.")
            .ValidateOnStart();

        services.AddOptions<FileAccessOptions>()
            .Bind(configuration.GetSection(FileAccessOptions.SectionName))
            .Validate(o => o.AllowedRoots != null && o.AllowedRoots.Any(r => !string.IsNullOrWhiteSpace(r)), "FileAccess:AllowedRoots must contain at least one valid root directory.")
            .Validate(o => o.MaxReadBytes > 0, "FileAccess:MaxReadBytes must be greater than 0.")
            .ValidateOnStart();

        services.AddOptions<AgentSecurityOptions>()
            .Bind(configuration.GetSection(AgentSecurityOptions.SectionName))
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

        services.AddSingleton<IPathPolicy, PathPolicy>();
        services.AddSingleton<IFileSystemExecutor, FileSystemExecutor>();
        services.AddSingleton<CommandHandler>();
        services.AddSingleton<GatewayConnection>();

        return services;
    }
}
