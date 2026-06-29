using System.Text.Json;
using LocalMcp.Agent.Windows.ProcessControl;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.Commands;

public sealed partial class CommandHandler
{
    private readonly IProcessManager? _processManager;

    private async Task<CommandResult<JsonElement>> HandleClipboardGetAsync(
        ClipboardGetCommand command,
        CancellationToken cancellationToken) =>
        ToJson(await PhaseFourDispatch.ClipboardGetAsync(_uiAutomationExecutor, command, cancellationToken));

    private async Task<CommandResult<JsonElement>> HandleClipboardSetAsync(
        ClipboardSetCommand command,
        CancellationToken cancellationToken) =>
        ToJson(await PhaseFourDispatch.ClipboardSetAsync(_uiAutomationExecutor, command, cancellationToken));

    private async Task<CommandResult<JsonElement>> HandleFileDialogSetPathAsync(
        FileDialogSetPathCommand command,
        CancellationToken cancellationToken) =>
        ToJson(await PhaseFourDispatch.FileDialogSetPathAsync(_uiAutomationExecutor, command, cancellationToken));

    private async Task<CommandResult<JsonElement>> HandleProcessListAsync(
        ProcessListCommand command,
        CancellationToken cancellationToken)
    {
        if (_processManager is null)
            return Unavailable(command.CommandId, "Process management is not configured on this agent.");

        return ToJson(await _processManager.ListAsync(
            command.NameContains,
            command.IncludeWindowless,
            command.MaxResults,
            command.CommandId,
            cancellationToken));
    }

    private async Task<CommandResult<JsonElement>> HandleProcessKillAsync(
        ProcessKillCommand command,
        CancellationToken cancellationToken)
    {
        if (_processManager is null)
            return Unavailable(command.CommandId, "Process management is not configured on this agent.");

        return ToJson(await _processManager.KillAsync(
            command.ProcessId,
            command.ExpectedProcessName,
            command.EntireProcessTree,
            command.TimeoutMs,
            command.CommandId,
            cancellationToken));
    }

    private static CommandResult<JsonElement> ToJson<T>(CommandResult<T> result)
    {
        if (!result.Success || result.Data is null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = result.CommandId,
                Success = false,
                Error = result.Error
            };
        }

        return new CommandResult<JsonElement>
        {
            CommandId = result.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(result.Data, JsonOptions.Default)
        };
    }

    private static CommandResult<JsonElement> Unavailable(Guid commandId, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(ErrorCodes.InternalError, message)
    };
}
