using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.AppLaunch;

public interface IAppLauncher
{
    Task<CommandResult<AppLaunchResult>> LaunchAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        bool waitForWindow,
        string? windowTitleContains,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken);
}
