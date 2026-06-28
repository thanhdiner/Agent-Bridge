using System.Text.Json;
using LocalMcp.Agent.Windows.AppLaunch;
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
    private readonly IAppLauncher? _appLauncher;
    private readonly IAppResolver? _appResolver;

    internal CommandHandler(
        IPathPolicy pathPolicy,
        IFileSystemExecutor fileSystemExecutor,
        IDirectoryCopyExecutor directoryCopyExecutor,
        PowerShellSessionRegistry sessionRegistry,
        PowerShellSessionExecutor sessionExecutor,
        IUiAutomationExecutor uiAutomationExecutor,
        IAppLauncher appLauncher,
        IAppResolver appResolver,
        ILogger<CommandHandler> logger)
        : this(
            pathPolicy,
            fileSystemExecutor,
            directoryCopyExecutor,
            sessionRegistry,
            sessionExecutor,
            uiAutomationExecutor,
            logger)
    {
        _appLauncher = appLauncher;
        _appResolver = appResolver;
    }

    private async Task<CommandResult<JsonElement>> HandleAppResolveAsync(
        AppResolveCommand command,
        CancellationToken cancellationToken)
    {
        if (_appResolver is null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(
                    ErrorCodes.AppResolveFailed,
                    "Application resolution is not configured on this agent.")
            };
        }

        var result = await _appResolver.ResolveAsync(
            command.AppId,
            command.Refresh,
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

    private async Task<CommandResult<JsonElement>> HandleAppLaunchAsync(
        AppLaunchCommand command,
        CancellationToken cancellationToken)
    {
        if (_appLauncher is null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(
                    ErrorCodes.AppLaunchFailed,
                    "Application launching is not configured on this agent.")
            };
        }

        var result = await _appLauncher.LaunchAsync(
            command.Executable,
            command.Arguments,
            command.WorkingDirectory,
            command.WaitForWindow,
            command.WindowTitleContains,
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
}
