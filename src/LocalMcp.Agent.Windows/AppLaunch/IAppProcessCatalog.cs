namespace LocalMcp.Agent.Windows.AppLaunch;

public interface IAppProcessCatalog
{
    int CurrentProcessId { get; }

    IAppProcess? GetById(int processId);

    IReadOnlyList<IAppProcess> GetByName(string processName);
}

public interface IAppProcess : IDisposable
{
    int Id { get; }

    string Name { get; }

    bool HasExited { get; }

    bool CloseMainWindow();

    void Kill(bool entireProcessTree);

    Task<bool> WaitForExitAsync(int timeoutMs, CancellationToken cancellationToken);
}
