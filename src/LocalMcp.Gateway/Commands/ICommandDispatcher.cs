using System.Text.Json;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Gateway.Commands;

public interface ICommandDispatcher
{
    Task<CommandResult<TResult>> SendAsync<TResult>(
        AgentCommand command,
        CancellationToken cancellationToken = default
    );

    void CompleteCommand(Guid commandId, CommandResult<JsonElement> result);
    void CancelPendingCommandsForDevice(string deviceId);
}
