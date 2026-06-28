using System.Text.Json;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.PowerShell;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using Microsoft.Extensions.Logging;

namespace LocalMcp.Agent.Windows.Commands;

public sealed partial class CommandHandler
{
    private readonly IUiAutomationExecutor? _uiAutomationExecutor;

    internal CommandHandler(
        IPathPolicy pathPolicy,
        IFileSystemExecutor fileSystemExecutor,
        IDirectoryCopyExecutor directoryCopyExecutor,
        PowerShellSessionRegistry sessionRegistry,
        PowerShellSessionExecutor sessionExecutor,
        IUiAutomationExecutor uiAutomationExecutor,
        ILogger<CommandHandler> logger)
        : this(
            pathPolicy,
            fileSystemExecutor,
            directoryCopyExecutor,
            sessionRegistry,
            sessionExecutor,
            logger)
    {
        _uiAutomationExecutor = uiAutomationExecutor;
    }

    private async Task<CommandResult<JsonElement>> HandleWindowListAsync(
        WindowListCommand command,
        CancellationToken cancellationToken)
    {
        if (_uiAutomationExecutor is null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(
                    ErrorCodes.UiAutomationUnavailable,
                    "Window enumeration is not configured on this agent.")
            };
        }

        var result = await _uiAutomationExecutor.ListWindowsAsync(
            command.IncludeInvisible,
            command.IncludeUntitled,
            command.MaxWindows,
            command.CommandId,
            cancellationToken);

        if (!result.Success || result.Data is null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = result.Error
            };
        }

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(
                result.Data,
                LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default)
        };
    }

    private async Task<CommandResult<JsonElement>> HandleWindowWaitAsync(
        WindowWaitCommand command,
        CancellationToken cancellationToken)
    {
        if (_uiAutomationExecutor is null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(
                    ErrorCodes.UiAutomationUnavailable,
                    "Window waiting is not configured on this agent.")
            };
        }

        var result = await _uiAutomationExecutor.WaitForWindowAsync(
            command.WindowHandle,
            command.ProcessId,
            command.ProcessName,
            command.ClassName,
            command.Title,
            command.TitleContains,
            command.OccurrenceIndex,
            command.Condition,
            command.ExpectedTitle,
            command.IncludeInvisible,
            command.TimeoutMs,
            command.PollIntervalMs,
            command.CommandId,
            cancellationToken);

        if (!result.Success || result.Data is null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = result.Error
            };
        }

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(
                result.Data,
                LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default)
        };
    }

    private async Task<CommandResult<JsonElement>> HandleUiFindAsync(
        UiFindCommand command,
        CancellationToken cancellationToken)
    {
        if (_uiAutomationExecutor is null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(
                    ErrorCodes.UiAutomationUnavailable,
                    "Windows UI Automation is not configured on this agent.")
            };
        }

        var result = await _uiAutomationExecutor.FindAsync(
            command.WindowHandle,
            command.AutomationId,
            command.NameContains,
            command.ControlType,
            command.MaxDepth,
            command.MaxResults,
            command.CommandId,
            cancellationToken);

        if (!result.Success || result.Data is null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = result.Error
            };
        }

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(
                result.Data,
                LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default)
        };
    }

    private async Task<CommandResult<JsonElement>> HandleUiTreeAsync(
        UiTreeCommand command,
        CancellationToken cancellationToken)
    {
        if (_uiAutomationExecutor is null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(
                    ErrorCodes.UiAutomationUnavailable,
                    "Windows UI Automation is not configured on this agent.")
            };
        }

        var result = await _uiAutomationExecutor.GetTreeAsync(
            command.WindowHandle,
            command.MaxDepth,
            command.MaxNodes,
            command.CommandId,
            cancellationToken);

        if (!result.Success || result.Data is null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = result.Error
            };
        }

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(
                result.Data,
                LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default)
        };
    }
}
