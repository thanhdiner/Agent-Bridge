using System.Diagnostics;

namespace LocalMcp.Agent.Windows.AppLaunch;

public sealed class AppProcessCatalog : IAppProcessCatalog
{
    public int CurrentProcessId => Environment.ProcessId;

    public IAppProcess? GetById(int processId)
    {
        try
        {
            return new AppProcess(Process.GetProcessById(processId));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public IReadOnlyList<IAppProcess> GetByName(string processName)
    {
        return Process.GetProcessesByName(processName)
            .OrderBy(process => process.Id)
            .Select(process => (IAppProcess)new AppProcess(process))
            .ToArray();
    }

    private sealed class AppProcess : IAppProcess
    {
        private readonly Process _process;

        public AppProcess(Process process)
        {
            _process = process;
        }

        public int Id => _process.Id;

        public string Name => _process.ProcessName;

        public bool HasExited => _process.HasExited;

        public bool CloseMainWindow() => _process.CloseMainWindow();

        public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

        public async Task<bool> WaitForExitAsync(
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            if (_process.HasExited)
                return true;
            if (timeoutMs <= 0)
                return false;

            try
            {
                await _process.WaitForExitAsync(cancellationToken).WaitAsync(
                    TimeSpan.FromMilliseconds(timeoutMs),
                    cancellationToken);
                return true;
            }
            catch (TimeoutException)
            {
                return _process.HasExited;
            }
        }

        public void Dispose() => _process.Dispose();
    }
}
