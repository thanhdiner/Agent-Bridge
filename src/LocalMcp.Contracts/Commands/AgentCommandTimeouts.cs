namespace LocalMcp.Contracts.Commands;

public static class AgentCommandTimeouts
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static TimeSpan GetTimeout(AgentCommand command)
    {
        if (command is GitLogCommand or GitShowCommand)
            return TimeSpan.FromSeconds(90);

        if (command is PowerShellExecuteCommand powerShellCommand)
        {
            var requestedPowerShellSeconds = Math.Clamp(
                powerShellCommand.TimeoutSeconds,
                1,
                900);
            return TimeSpan.FromSeconds(requestedPowerShellSeconds + 15);
        }

        // powershell_start: fire-and-return, the agent launches the process and
        // responds immediately; 15 s is ample for process startup handshake.
        if (command is PowerShellStartCommand)
            return TimeSpan.FromSeconds(15);

        // powershell_status / powershell_cancel: lightweight in-memory poll.
        if (command is PowerShellStatusCommand or PowerShellCancelCommand)
            return TimeSpan.FromSeconds(10);

        if (command is AppOpenCommand appOpenCommand)
        {
            if (!appOpenCommand.WaitForWindow)
                return TimeSpan.FromSeconds(20);

            var requestedWaitMilliseconds = Math.Clamp(
                appOpenCommand.TimeoutMs,
                1,
                300_000);
            return TimeSpan.FromMilliseconds(requestedWaitMilliseconds + 15_000);
        }

        if (command is AppCloseCommand appCloseCommand)
        {
            var requestedWaitMilliseconds = Math.Clamp(
                appCloseCommand.TimeoutMs,
                1,
                300_000);
            return TimeSpan.FromMilliseconds(requestedWaitMilliseconds + 10_000);
        }

        if (command is AppLaunchCommand appLaunchCommand)
        {
            if (!appLaunchCommand.WaitForWindow)
                return TimeSpan.FromSeconds(15);

            var requestedWaitMilliseconds = Math.Clamp(
                appLaunchCommand.TimeoutMs,
                1,
                300_000);
            return TimeSpan.FromMilliseconds(requestedWaitMilliseconds + 10_000);
        }

        if (command is ProcessWaitCommand processWaitCommand)
        {
            var requestedWaitMilliseconds = Math.Clamp(
                processWaitCommand.TimeoutMs,
                1,
                300_000);
            return TimeSpan.FromMilliseconds(requestedWaitMilliseconds + 10_000);
        }

        if (command is UiWaitCommand uiWaitCommand)
        {
            var requestedWaitMilliseconds = Math.Clamp(
                uiWaitCommand.TimeoutMs,
                1,
                300_000);
            return TimeSpan.FromMilliseconds(requestedWaitMilliseconds + 10_000);
        }

        if (command is WindowWaitCommand windowWaitCommand)
        {
            var requestedWaitMilliseconds = Math.Clamp(
                windowWaitCommand.TimeoutMs,
                1,
                300_000);
            return TimeSpan.FromMilliseconds(requestedWaitMilliseconds + 10_000);
        }

        if (command is not ProjectCheckCommand projectCommand)
            return DefaultTimeout;

        var requestedSeconds = Math.Clamp(projectCommand.TimeoutSeconds, 30, 900);
        return TimeSpan.FromSeconds(requestedSeconds + 15);
    }
}
