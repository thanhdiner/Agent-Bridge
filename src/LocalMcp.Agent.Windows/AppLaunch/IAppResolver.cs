using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.AppLaunch;

public interface IAppResolver
{
    Task<CommandResult<AppResolveResult>> ResolveAsync(
        string appId,
        bool refresh,
        Guid commandId,
        CancellationToken cancellationToken);
}
