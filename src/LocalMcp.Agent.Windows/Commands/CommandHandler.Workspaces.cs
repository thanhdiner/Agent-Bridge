using System.Text.Json;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.Agent.Windows.Workspaces;

namespace LocalMcp.Agent.Windows.Commands;

public sealed partial class CommandHandler
{
    private readonly IWorkspaceResolver? _workspaceResolver;

    private Task<CommandResult<JsonElement>> HandleWorkspaceListAsync(
        WorkspaceListCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_workspaceResolver is null)
            return Task.FromResult(WorkspaceFailure(
                command.CommandId,
                ErrorCodes.InternalError,
                "Workspace resolution is not configured on this agent."));

        return Task.FromResult(new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(
                _workspaceResolver.List(),
                LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default)
        });
    }

    private Task<CommandResult<JsonElement>> HandleWorkspaceResolveAsync(
        WorkspaceResolveCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_workspaceResolver is null)
            return Task.FromResult(WorkspaceFailure(
                command.CommandId,
                ErrorCodes.InternalError,
                "Workspace resolution is not configured on this agent."));

        var outcome = _workspaceResolver.Resolve(
            command.Alias,
            command.RelativePath,
            command.RequireWritable);

        if (outcome.Error is not null || outcome.Data is null)
            return Task.FromResult(WorkspaceFailure(
                command.CommandId,
                outcome.Error?.Code ?? ErrorCodes.InternalError,
                outcome.Error?.Message ?? "Workspace resolution failed."));

        return Task.FromResult(new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(
                outcome.Data,
                LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default)
        });
    }

    private static CommandResult<JsonElement> WorkspaceFailure(
        Guid commandId,
        string code,
        string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message),
        Data = JsonSerializer.SerializeToElement<object?>(null)
    };
}
