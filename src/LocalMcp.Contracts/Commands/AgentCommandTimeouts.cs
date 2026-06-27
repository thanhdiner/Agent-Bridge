namespace LocalMcp.Contracts.Commands;

public static class AgentCommandTimeouts
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static TimeSpan GetTimeout(AgentCommand command)
    {
        if (command is GitLogCommand or GitShowCommand)
            return TimeSpan.FromSeconds(90);

        if (command is not ProjectCheckCommand projectCommand)
            return DefaultTimeout;

        var requestedSeconds = Math.Clamp(projectCommand.TimeoutSeconds, 30, 900);
        return TimeSpan.FromSeconds(requestedSeconds + 15);
    }
}
