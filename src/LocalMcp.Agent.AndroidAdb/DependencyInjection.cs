using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LocalMcp.Agent.AndroidAdb;

public static class DependencyInjection
{
    public static IServiceCollection AddAndroidAdbAgent(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AndroidAdbOptions>()
            .Bind(configuration.GetSection(AndroidAdbOptions.SectionName))
            .Validate(options => Uri.TryCreate(options.GatewayUrl, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https", "AndroidAdb:GatewayUrl must be an HTTP/HTTPS URL.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.AdbPath), "AndroidAdb:AdbPath is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Serial)
                && options.Serial.Length <= 256
                && !options.Serial.Any(char.IsControl), "AndroidAdb:Serial is required and must be a valid ADB serial.")
            .Validate(options => string.IsNullOrWhiteSpace(options.DeviceId)
                || options.DeviceId.Length <= 256 && !options.DeviceId.Any(char.IsControl), "AndroidAdb:DeviceId is invalid.")
            .Validate(options => options.CommandTimeoutSeconds is >= 5 and <= 120, "AndroidAdb:CommandTimeoutSeconds must be between 5 and 120.")
            .Validate(options => options.MaxScreenshotBytes is >= 1024 and <= 6 * 1024 * 1024, "AndroidAdb:MaxScreenshotBytes must be between 1KB and 6MB.")
            .ValidateOnStart();

        services.AddOptions<AgentSecurityOptions>()
            .Bind(configuration.GetSection(AgentSecurityOptions.SectionName))
            .Validate(options => !options.AuthenticationEnabled
                || !string.IsNullOrWhiteSpace(options.TokenEnvironmentVariable), "AgentSecurity token environment variable is required when authentication is enabled.")
            .ValidateOnStart();

        services.AddSingleton<IAdbExecutor, AdbProcessExecutor>();
        services.AddSingleton<AndroidCommandHandler>();
        services.AddSingleton<AndroidGatewayConnection>();
        return services;
    }
}
