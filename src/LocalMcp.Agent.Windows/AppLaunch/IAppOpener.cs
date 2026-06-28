using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.AppLaunch;

public interface IAppOpener
{
    Task<CommandResult<AppOpenResult>> OpenAsync(
        string appId,
        IReadOnlyList<string> arguments,
        bool refresh,
        bool waitForWindow,
        string? windowTitleContains,
        int timeoutMs,
        int pollIntervalMs,
        Guid commandId,
        CancellationToken cancellationToken);
}
