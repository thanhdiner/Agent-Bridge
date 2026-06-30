using LocalMcp.Agent.Windows.AppLaunch;
using LocalMcp.Agent.Windows.Connection;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.Commands;
using LocalMcp.Agent.Windows.PowerShell;
using LocalMcp.Agent.Windows.ProcessControl;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.Agent.Windows.Workspaces;
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
            .Validate(o => o.MaxWriteBytes > 0, "FileAccess:MaxWriteBytes must be greater than 0.")
            .Validate(o =>
            {
                if (o.WritableRoots == null || o.WritableRoots.Count == 0)
                {
                    return true;
                }
                foreach (var wRoot in o.WritableRoots)
                {
                    if (string.IsNullOrWhiteSpace(wRoot)) continue;
                    try
                    {
                        var fullWR = Path.GetFullPath(wRoot);
                        var matched = o.AllowedRoots.Any(aRoot =>
                        {
                            if (string.IsNullOrWhiteSpace(aRoot)) return false;
                            try
                            {
                                var fullAR = Path.GetFullPath(aRoot);
                                return PathPolicy.IsSubdirectoryOf(fullWR, fullAR);
                            }
                            catch { return false; }
                        });
                        if (!matched) return false;
                    }
                    catch { return false; }
                }
                return true;
            }, "All WritableRoots must be located within at least one of the configured AllowedRoots.")
            .ValidateOnStart();

        services.AddOptions<WorkspaceOptions>()
            .Bind(configuration.GetSection(WorkspaceOptions.SectionName))
            .Validate(o => o.Aliases is not null && o.Aliases.Count <= 64,
                "Workspaces:Aliases must contain at most 64 entries.")
            .Validate(o => o.Aliases is not null
                && o.Aliases.Keys
                    .Select(alias => alias.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() == o.Aliases.Count,
                "Workspaces:Aliases must be unique ignoring case.")
            .Validate(o => o.Aliases is not null && o.Aliases.All(pair =>
                WorkspaceOptions.IsValidAlias(pair.Key)
                && pair.Value is not null
                && IsValidWorkspaceDefinition(pair.Value)),
                "Workspaces:Aliases contains an invalid alias or workspace definition.")
            .ValidateOnStart();

        services.AddOptions<AppLaunchOptions>()
            .Bind(configuration.GetSection(AppLaunchOptions.SectionName))
            .Validate(o => o.AllowedExecutables is not null, "AppLaunch:AllowedExecutables must be configured as an array.")
            .Validate(o => o.AllowedExecutables.All(entry =>
                !string.IsNullOrWhiteSpace(entry)
                && entry.Length <= 32768
                && !entry.Any(char.IsControl)),
                "AppLaunch:AllowedExecutables contains an invalid entry.")
            .ValidateOnStart();

        services.AddOptions<AppResolverOptions>()
            .Bind(configuration.GetSection(AppResolverOptions.SectionName))
            .Validate(o => o.MaxCacheEntries is >= 1 and <= 1024,
                "AppResolver:MaxCacheEntries must be between 1 and 1024.")
            .Validate(o => o.MaxStartMenuShortcuts is >= 0 and <= 10000,
                "AppResolver:MaxStartMenuShortcuts must be between 0 and 10000.")
            .Validate(o => o.Aliases is not null
                && o.Aliases.All(pair =>
                    !string.IsNullOrWhiteSpace(pair.Key)
                    && pair.Key.Length <= 128
                    && !pair.Key.Any(char.IsControl)
                    && !string.IsNullOrWhiteSpace(pair.Value)
                    && pair.Value.Length <= 32768
                    && !pair.Value.Any(char.IsControl)),
                "AppResolver:Aliases contains an invalid entry.")
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

        services.AddSingleton<IWorkspaceResolver, WorkspaceResolver>();
        services.AddSingleton<IPathPolicy, PathPolicy>();
        services.AddSingleton<IFileSystemExecutor, FileSystemExecutor>();
        services.AddSingleton<IDirectoryCopyExecutor, DirectoryCopyExecutor>();
        services.AddSingleton<PowerShellSessionRegistry>();
        services.AddSingleton<IPowerShellSessionCoordinator>(sp =>
            sp.GetRequiredService<PowerShellSessionRegistry>());
        services.AddSingleton<PowerShellSessionExecutor>();
        services.AddSingleton<IUiAutomationExecutor, UiAutomationExecutor>();
        services.AddSingleton<IAppResolver, AppResolver>();
        services.AddSingleton<IAppLauncher, AppLauncher>();
        services.AddSingleton<IAppOpener, AppOpener>();
        services.AddSingleton<IAppProcessCatalog, AppProcessCatalog>();
        services.AddSingleton<IAppCloser, AppCloser>();
        services.AddSingleton<IProcessWaiter, ProcessWaiter>();
        services.AddSingleton<IProcessManager, ProcessManager>();
        services.AddSingleton<CommandHandler>(sp =>
            new CommandHandler(
                sp.GetRequiredService<IPathPolicy>(),
                sp.GetRequiredService<IFileSystemExecutor>(),
                sp.GetRequiredService<IDirectoryCopyExecutor>(),
                sp.GetRequiredService<PowerShellSessionRegistry>(),
                sp.GetRequiredService<PowerShellSessionExecutor>(),
                sp.GetRequiredService<IUiAutomationExecutor>(),
                sp.GetRequiredService<IAppLauncher>(),
                sp.GetRequiredService<IAppResolver>(),
                sp.GetRequiredService<IAppOpener>(),
                sp.GetRequiredService<IAppCloser>(),
                sp.GetRequiredService<IProcessWaiter>(),
                sp.GetRequiredService<IProcessManager>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CommandHandler>>(),
                sp.GetRequiredService<IWorkspaceResolver>()));
        services.AddSingleton<GatewayConnection>();

        return services;
    }

    private static bool IsValidWorkspaceDefinition(WorkspaceDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Path)
            || definition.Path.Length > 32_768
            || definition.Path.Any(char.IsControl)
            || !Path.IsPathFullyQualified(definition.Path))
        {
            return false;
        }

        if (definition.Description is { Length: > 256 }
            || (definition.Description?.Any(char.IsControl) ?? false))
        {
            return false;
        }

        try
        {
            _ = Path.GetFullPath(definition.Path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

