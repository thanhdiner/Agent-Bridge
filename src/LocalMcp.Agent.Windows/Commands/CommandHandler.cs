using System.Text;
using System.Text.Json;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.PowerShell;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Logging;

namespace LocalMcp.Agent.Windows.Commands;

public sealed partial class CommandHandler
{
    private readonly IPathPolicy _pathPolicy;
    private readonly IFileSystemExecutor _fileSystemExecutor;
    private readonly IDirectoryCopyExecutor _directoryCopyExecutor;
    private readonly PowerShellSessionRegistry? _sessionRegistry;
    private readonly PowerShellSessionExecutor? _sessionExecutor;
    private readonly ILogger<CommandHandler> _logger;

    internal Func<string, Task>? OnBeforeMultiFileEditHook { get; set; }

    public CommandHandler(
        IPathPolicy pathPolicy,
        IFileSystemExecutor fileSystemExecutor,
        ILogger<CommandHandler> logger)
        : this(
            pathPolicy,
            fileSystemExecutor,
            new FileCopyFallbackExecutor(fileSystemExecutor),
            logger)
    {
    }

    public CommandHandler(
        IPathPolicy pathPolicy,
        IFileSystemExecutor fileSystemExecutor,
        IDirectoryCopyExecutor directoryCopyExecutor,
        ILogger<CommandHandler> logger)
    {
        _pathPolicy = pathPolicy;
        _fileSystemExecutor = fileSystemExecutor;
        _directoryCopyExecutor = directoryCopyExecutor;
        _logger = logger;
    }

    internal CommandHandler(
        IPathPolicy pathPolicy,
        IFileSystemExecutor fileSystemExecutor,
        IDirectoryCopyExecutor directoryCopyExecutor,
        PowerShellSessionRegistry sessionRegistry,
        PowerShellSessionExecutor sessionExecutor,
        ILogger<CommandHandler> logger)
    {
        _pathPolicy = pathPolicy;
        _fileSystemExecutor = fileSystemExecutor;
        _directoryCopyExecutor = directoryCopyExecutor;
        _sessionRegistry = sessionRegistry;
        _sessionExecutor = sessionExecutor;
        _logger = logger;
    }

    public async Task<CommandResult<JsonElement>> HandleAsync(
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Handling command {CommandId} (Type: {CommandType})", command.CommandId, command.GetType().Name);

        if (command is ReadFileCommand readFileCommand)
        {
            return await HandleReadFileAsync(readFileCommand, cancellationToken);
        }
        else if (command is ReadRangeCommand readRangeCommand)
        {
            return await HandleReadRangeAsync(readRangeCommand, cancellationToken);
        }
        else if (command is ListDirectoryCommand listDirectoryCommand)
        {
            return await HandleListDirectoryAsync(listDirectoryCommand, cancellationToken);
        }
        else if (command is SearchFilesCommand searchFilesCommand)
        {
            return await HandleSearchFilesAsync(searchFilesCommand, cancellationToken);
        }
        else if (command is SearchContextCommand searchContextCommand)
        {
            return await HandleSearchContextAsync(searchContextCommand, cancellationToken);
        }
        else if (command is GitStatusCommand gitStatusCommand)
        {
            return await HandleGitStatusAsync(gitStatusCommand, cancellationToken);
        }
        else if (command is GitDiffCommand gitDiffCommand)
        {
            return await HandleGitDiffAsync(gitDiffCommand, cancellationToken);
        }
        else if (command is GitLogCommand gitLogCommand)
        {
            return await HandleGitLogAsync(gitLogCommand, cancellationToken);
        }
        else if (command is GitShowCommand gitShowCommand)
        {
            return await HandleGitShowAsync(gitShowCommand, cancellationToken);
        }
        else if (command is ProjectCheckCommand projectCheckCommand)
        {
            return await HandleProjectCheckAsync(projectCheckCommand, cancellationToken);
        }
        else if (command is PowerShellExecuteCommand powerShellExecuteCommand)
        {
            return await HandlePowerShellExecuteAsync(
                powerShellExecuteCommand,
                cancellationToken);
        }
        else if (command is PowerShellStartCommand psStartCommand)
        {
            return await HandlePowerShellStartAsync(psStartCommand, cancellationToken);
        }
        else if (command is PowerShellStatusCommand psStatusCommand)
        {
            return await HandlePowerShellStatusAsync(psStatusCommand, cancellationToken);
        }
        else if (command is PowerShellCancelCommand psCancelCommand)
        {
            return await HandlePowerShellCancelAsync(psCancelCommand, cancellationToken);
        }
        else if (command is TreeCommand treeCommand)
        {
            return await HandleTreeAsync(treeCommand, cancellationToken);
        }
        else if (command is WriteFileCommand writeFileCommand)
        {
            return await HandleWriteFileAsync(writeFileCommand, cancellationToken);
        }
        else if (command is PatchFileCommand patchFileCommand)
        {
            return await HandlePatchFileAsync(patchFileCommand, cancellationToken);
        }
        else if (command is MultiFilePatchCommand batchPatchCommand)
        {
            return await HandleBatchPatchAsync(batchPatchCommand, cancellationToken);
        }
        else if (command is CreateDirectoryCommand createDirectoryCommand)
        {
            return await HandleCreateDirectoryAsync(createDirectoryCommand, cancellationToken);
        }
        else if (command is StatCommand statCommand)
        {
            return await HandleStatAsync(statCommand, cancellationToken);
        }
        else if (command is BatchStatCommand batchStatCommand)
        {
            return await HandleBatchStatAsync(batchStatCommand, cancellationToken);
        }
        else if (command is BatchReadCommand batchReadCommand)
        {
            return await HandleBatchReadAsync(batchReadCommand, cancellationToken);
        }
        else if (command is MoveCommand moveCommand)
        {
            return await HandleMoveAsync(moveCommand, cancellationToken);
        }
        else if (command is CopyCommand copyCommand)
        {
            return await HandleCopyAsync(copyCommand, cancellationToken);
        }
        else if (command is DeleteCommand deleteCommand)
        {
            return await HandleDeleteAsync(deleteCommand, cancellationToken);
        }
        else if (command is RemoveDirectoryCommand removeDirectoryCommand)
        {
            return await HandleRemoveDirectoryAsync(removeDirectoryCommand, cancellationToken);
        }

        _logger.LogWarning("Unsupported command type received: {CommandType}", command.GetType().Name);
        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = false,
            Error = new CommandError(ErrorCodes.UnsupportedCommand, $"Command type '{command.GetType().Name}' is not supported."),
            Data = JsonSerializer.SerializeToElement<object?>(null)
        };
    }

    private async Task<CommandResult<JsonElement>> HandleTreeAsync(
        TreeCommand command,
        CancellationToken cancellationToken)
    {
        // Use explicit read-directory authorization
        var error = _pathPolicy.AuthorizeReadDirectory(command.Path, out var normalizedPath);
        if (error is not null)
        {
            _logger.LogWarning("Path validation failed for tree command {CommandId}: {ErrorCode} - {ErrorMessage}", command.CommandId, error.Code, error.Message);
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        var treeResult = await _fileSystemExecutor.GetTreeAsync(
            normalizedPath,
            command.MaxDepth,
            command.MaxEntries,
            command.IncludeHidden,
            command.CommandId,
            cancellationToken
        );

        if (!treeResult.Success || treeResult.Data == null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = treeResult.Error
            };
        }

        var dataJson = JsonSerializer.SerializeToElement(treeResult.Data, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };
    }

    private async Task<CommandResult<JsonElement>> HandleListDirectoryAsync(
        ListDirectoryCommand command,
        CancellationToken cancellationToken)
    {
        // Use explicit read-directory authorization
        var error = _pathPolicy.AuthorizeReadDirectory(command.Path, out var normalizedPath);
        if (error is not null)
        {
            _logger.LogWarning("Path validation failed for list command {CommandId}: {ErrorCode} - {ErrorMessage}", command.CommandId, error.Code, error.Message);
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        var listResult = await _fileSystemExecutor.ListDirectoryAsync(normalizedPath, command.MaxEntries, command.CommandId, cancellationToken);

        if (!listResult.Success || listResult.Data == null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = listResult.Error
            };
        }

        // Filter directories and files with explicit path policy checks
        var filteredDirs = new List<DirectoryEntry>();
        foreach (var dir in listResult.Data.Directories)
        {
            var itemError = _pathPolicy.AuthorizeReadDirectory(dir.Path, out _);
            if (itemError is null)
            {
                filteredDirs.Add(dir);
            }
        }

        var filteredFiles = new List<FileEntry>();
        foreach (var file in listResult.Data.Files)
        {
            var itemError = _pathPolicy.AuthorizeReadFile(file.Path, out _);
            if (itemError is null)
            {
                filteredFiles.Add(file);
            }
        }

        var filteredResult = new ListDirectoryResult
        {
            NormalizedPath = listResult.Data.NormalizedPath,
            Directories = filteredDirs,
            Files = filteredFiles,
            TotalDirectories = filteredDirs.Count,
            TotalFiles = filteredFiles.Count
        };

        var dataJson = JsonSerializer.SerializeToElement(filteredResult, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };
    }

    private async Task<CommandResult<JsonElement>> HandleSearchFilesAsync(
        SearchFilesCommand command,
        CancellationToken cancellationToken)
    {
        // Use explicit read-directory authorization
        var error = _pathPolicy.AuthorizeReadDirectory(command.Path, out var normalizedPath);
        if (error is not null)
        {
            _logger.LogWarning("Path validation failed for search command {CommandId}: {ErrorCode} - {ErrorMessage}", command.CommandId, error.Code, error.Message);
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        var searchResult = await _fileSystemExecutor.SearchFilesAsync(
            normalizedPath,
            command.Query,
            command.MaxResults,
            command.MaxDepth,
            command.CommandId,
            cancellationToken
        );

        if (!searchResult.Success || searchResult.Data == null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = searchResult.Error
            };
        }

        // Filter files with explicit path policy checks
        var filteredMatches = new List<SearchMatch>();
        foreach (var item in searchResult.Data.Matches)
        {
            var itemError = _pathPolicy.AuthorizeReadFile(item.FullPath, out _);
            if (itemError is null)
            {
                filteredMatches.Add(item);
            }
        }

        var filteredResult = new SearchFilesResult
        {
            Matches = filteredMatches
        };

        var dataJson = JsonSerializer.SerializeToElement(filteredResult, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };
    }

    private async Task<CommandResult<JsonElement>> HandleSearchContextAsync(
        SearchContextCommand command,
        CancellationToken cancellationToken)
    {
        var error = _pathPolicy.AuthorizeReadDirectory(command.Path, out var normalizedPath);
        if (error is not null)
        {
            _logger.LogWarning("Path validation failed for contextual search command {CommandId}: {ErrorCode} - {ErrorMessage}", command.CommandId, error.Code, error.Message);
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        var result = await _fileSystemExecutor.SearchContextAsync(
            normalizedPath,
            command.Query,
            command.UseRegex,
            command.CaseSensitive,
            command.IncludeGlobs ?? [],
            command.ExcludeGlobs ?? [],
            command.ContextBefore,
            command.ContextAfter,
            command.MaxResults,
            command.MaxDepth,
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

        var filteredMatches = result.Data.Matches
            .Where(match => _pathPolicy.AuthorizeReadFile(match.FullPath, out _) is null)
            .ToList();

        var filteredResult = new SearchContextResult
        {
            Matches = filteredMatches,
            Truncated = result.Data.Truncated
        };

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(
                filteredResult,
                LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default)
        };
    }

    private async Task<CommandResult<JsonElement>> HandleGitStatusAsync(
        GitStatusCommand command,
        CancellationToken cancellationToken)
    {
        var error = _pathPolicy.AuthorizeReadDirectory(command.Path, out var normalizedPath);
        if (error is not null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        var result = await _fileSystemExecutor.GitStatusAsync(
            normalizedPath,
            command.IncludeUntracked,
            command.MaxEntries,
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

    private async Task<CommandResult<JsonElement>> HandleGitDiffAsync(
        GitDiffCommand command,
        CancellationToken cancellationToken)
    {
        var error = _pathPolicy.AuthorizeReadDirectory(command.Path, out var normalizedPath);
        if (error is not null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        var result = await _fileSystemExecutor.GitDiffAsync(
            normalizedPath,
            command.Staged,
            command.IncludeUntracked,
            command.PathSpecs ?? [],
            command.ContextLines,
            command.MaxBytes,
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

    private async Task<CommandResult<JsonElement>> HandleGitLogAsync(
        GitLogCommand command,
        CancellationToken cancellationToken)
    {
        var error = _pathPolicy.AuthorizeReadDirectory(command.Path, out var normalizedPath);
        if (error is not null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        var result = await _fileSystemExecutor.GitLogAsync(
            normalizedPath,
            command.MaxCount,
            command.Skip,
            command.PathSpec,
            command.Author,
            command.Since,
            command.Until,
            command.IncludeStats,
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

    private async Task<CommandResult<JsonElement>> HandleGitShowAsync(
        GitShowCommand command,
        CancellationToken cancellationToken)
    {
        var error = _pathPolicy.AuthorizeReadDirectory(command.Path, out var normalizedPath);
        if (error is not null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        var result = await _fileSystemExecutor.GitShowAsync(
            normalizedPath,
            command.Revision,
            command.PathSpecs ?? [],
            command.IncludePatch,
            command.IncludeStats,
            command.ContextLines,
            command.MaxBytes,
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

    private async Task<CommandResult<JsonElement>> HandleProjectCheckAsync(
        ProjectCheckCommand command,
        CancellationToken cancellationToken)
    {
        var error = _pathPolicy.AuthorizeReadDirectory(command.Path, out var normalizedPath);
        if (error is not null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        var result = await _fileSystemExecutor.ProjectCheckAsync(
            normalizedPath,
            command.ProjectType,
            command.Steps ?? [],
            command.Configuration,
            command.TimeoutSeconds,
            command.MaxOutputBytes,
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

    private async Task<CommandResult<JsonElement>> HandlePowerShellExecuteAsync(
        PowerShellExecuteCommand command,
        CancellationToken cancellationToken)
    {
        var error = _pathPolicy.AuthorizeCreateDirectory(
            command.WorkingDirectory,
            out var normalizedPath,
            recursive: false);
        if (error is not null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        if (!Directory.Exists(normalizedPath))
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(
                    ErrorCodes.DirectoryNotFound,
                    "The PowerShell working directory was not found."),
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        var result = await _fileSystemExecutor.PowerShellExecuteAsync(
            normalizedPath,
            command.Script,
            command.Visible,
            command.Elevated,
            command.TimeoutSeconds,
            command.MaxOutputBytes,
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
    private async Task<CommandResult<JsonElement>> HandleReadFileAsync(
        ReadFileCommand command,
        CancellationToken cancellationToken)
    {
        // Use explicit read-file authorization
        var error = _pathPolicy.AuthorizeReadFile(command.Path, out var normalizedPath);
        if (error is not null)
        {
            _logger.LogWarning("Path validation failed for read command {CommandId}: {ErrorCode} - {ErrorMessage}", command.CommandId, error.Code, error.Message);
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        var readResult = await _fileSystemExecutor.ReadFileAsync(normalizedPath, command.CommandId, cancellationToken);

        if (!readResult.Success)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = readResult.Error
            };
        }

        var dataJson = JsonSerializer.SerializeToElement(readResult.Data, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };
    }

    private async Task<CommandResult<JsonElement>> HandleReadRangeAsync(
        ReadRangeCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _fileSystemExecutor.ReadRangeAsync(
            command.Path,
            command.StartLine,
            command.LineCount,
            command.CommandId,
            cancellationToken
        );

        if (!result.Success || result.Data == null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = result.Error
            };
        }

        var dataJson = JsonSerializer.SerializeToElement(result.Data, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);
        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };
    }

    private async Task<CommandResult<JsonElement>> HandleWriteFileAsync(
        WriteFileCommand command,
        CancellationToken cancellationToken)
    {
        // Use explicit write-file authorization (file does not need to exist yet)
        var error = _pathPolicy.AuthorizeWriteFile(command.Path, out var normalizedPath, mustExist: false);
        if (error is not null)
        {
            _logger.LogWarning("Path validation failed for write command {CommandId}: {ErrorCode} - {ErrorMessage}", command.CommandId, error.Code, error.Message);
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        var writeResult = await _fileSystemExecutor.WriteFileAsync(
            normalizedPath,
            command.Content,
            command.ExpectedSha256,
            command.CreateIfMissing,
            command.CommandId,
            cancellationToken
        );

        if (!writeResult.Success || writeResult.Data == null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = writeResult.Error
            };
        }

        var dataJson = JsonSerializer.SerializeToElement(writeResult.Data, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };
    }

    private async Task<CommandResult<JsonElement>> HandlePatchFileAsync(
        PatchFileCommand command,
        CancellationToken cancellationToken)
    {
        // Use explicit write-file authorization (target file MUST exist)
        var error = _pathPolicy.AuthorizeWriteFile(command.Path, out var normalizedPath, mustExist: true);
        if (error is not null)
        {
            _logger.LogWarning("Path validation failed for patch command {CommandId}: {ErrorCode} - {ErrorMessage}", command.CommandId, error.Code, error.Message);
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        var patchResult = await _fileSystemExecutor.PatchFileAsync(
            normalizedPath,
            command.ExpectedSha256,
            command.Edits,
            command.CommandId,
            cancellationToken
        );

        if (!patchResult.Success || patchResult.Data == null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = patchResult.Error
            };
        }

        var dataJson = JsonSerializer.SerializeToElement(patchResult.Data, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };
    }

    private async Task<CommandResult<JsonElement>> HandleBatchPatchAsync(
        MultiFilePatchCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Items is null || command.Items.Count < 1 || command.Items.Count > 20)
        {
            return BatchPatchFailure(
                command.CommandId,
                ErrorCodes.InvalidRequest,
                "items must contain between 1 and 20 entries.");
        }

        var itemResults = new MultiFilePatchItemResult[command.Items.Count];
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = command.Items.Select((item, index) =>
            ProcessMultiFilePatchItemAsync(command, item, index, itemResults, gate, cancellationToken));

        try
        {
            await Task.WhenAll(tasks);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return BatchPatchFailure(command.CommandId, ErrorCodes.CommandCancelled, "The batch patch command was cancelled.");
        }

        var data = new MultiFilePatchResult
        {
            Items = itemResults,
            Succeeded = itemResults.Count(item => item.Success),
            Failed = itemResults.Count(item => !item.Success)
        };

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(data, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default)
        };
    }

    private async Task ProcessMultiFilePatchItemAsync(
        MultiFilePatchCommand command,
        MultiFilePatchItem? item,
        int index,
        MultiFilePatchItemResult[] results,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (OnBeforeMultiFileEditHook is not null)
                await OnBeforeMultiFileEditHook(item?.Path ?? string.Empty);

            results[index] = await PatchBatchItemAsync(command, item, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<MultiFilePatchItemResult> PatchBatchItemAsync(
        MultiFilePatchCommand command,
        MultiFilePatchItem? item,
        CancellationToken cancellationToken)
    {
        var path = item?.Path ?? string.Empty;
        if (item is null)
            return MultiFilePatchItemFailure(path, ErrorCodes.InvalidRequest, "The batch patch item is required.");

        return await ExecuteMultiFilePatchItemAsync(command, item, path, cancellationToken);
    }

    private async Task<MultiFilePatchItemResult> ExecuteMultiFilePatchItemAsync(
        MultiFilePatchCommand command,
        MultiFilePatchItem item,
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.ExpectedSha256))
            return MultiFilePatchItemFailure(path, ErrorCodes.ExpectedHashRequired, "expectedSha256 is required.");

        if (item.Edits is null || item.Edits.Count == 0)
            return MultiFilePatchItemFailure(path, ErrorCodes.PatchEditsRequired, "At least one patch edit is required.");

        return await RunBatchItemAsync(command, item, path, cancellationToken);
    }

    private Task<MultiFilePatchItemResult> RunBatchItemAsync(
        MultiFilePatchCommand command,
        MultiFilePatchItem item,
        string path,
        CancellationToken cancellationToken)
    {
        var single = new PatchFileCommand
        {
            CommandId = command.CommandId,
            DeviceId = command.DeviceId,
            CreatedAt = command.CreatedAt,
            Path = path,
            ExpectedSha256 = item.ExpectedSha256,
            Edits = item.Edits
        };
        return ConvertBatchPatchResultAsync(path, single, cancellationToken);
    }

    private async Task<MultiFilePatchItemResult> ConvertBatchPatchResultAsync(
        string path,
        PatchFileCommand command,
        CancellationToken cancellationToken)
    {
        var result = await HandlePatchFileAsync(command, cancellationToken);
        return ConvertBatchPatchResult(path, result);
    }

    private static MultiFilePatchItemResult ConvertBatchPatchResult(
        string path,
        CommandResult<JsonElement> result)
    {
        if (!result.Success)
            return new MultiFilePatchItemResult { Path = path, Success = false, Error = result.Error };

        return ReadBatchPatchData(path, result.Data);
    }

    private static MultiFilePatchItemResult ReadBatchPatchData(string path, JsonElement dataElement)
    {
        var data = dataElement.Deserialize<PatchFileResult>(
            LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);
        return data is null
            ? MultiFilePatchItemFailure(path, ErrorCodes.InternalError, "The file patch did not return data.")
            : new MultiFilePatchItemResult { Path = path, Success = true, Data = data };
    }

    private static MultiFilePatchItemResult MultiFilePatchItemFailure(string path, string code, string message) => new()
    {
        Path = path,
        Success = false,
        Error = new CommandError(code, message)
    };

    private static CommandResult<JsonElement> BatchPatchFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };

    private async Task<CommandResult<JsonElement>> HandleCreateDirectoryAsync(
        CreateDirectoryCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _fileSystemExecutor.CreateDirectoryAsync(
            command.Path,
            command.Recursive,
            command.CommandId,
            cancellationToken
        );

        if (!result.Success || result.Data == null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = result.Error
            };
        }

        var dataJson = JsonSerializer.SerializeToElement(result.Data, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };
    }

    private async Task<CommandResult<JsonElement>> HandleStatAsync(
        StatCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _fileSystemExecutor.StatAsync(
            command.Path,
            command.CommandId,
            cancellationToken
        );

        if (!result.Success || result.Data == null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = result.Error
            };
        }

        var dataJson = JsonSerializer.SerializeToElement(result.Data, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };
    }

    private async Task<CommandResult<JsonElement>> HandleBatchStatAsync(
        BatchStatCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Paths is null || command.Paths.Count < 1 || command.Paths.Count > 100)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InvalidRequest, "paths must contain between 1 and 100 entries.")
            };
        }

        var items = new BatchStatItemResult[command.Paths.Count];
        using var concurrencyGate = new SemaphoreSlim(initialCount: 8, maxCount: 8);

        try
        {
            var tasks = command.Paths.Select((path, index) => ProcessPathAsync(path, index)).ToArray();
            await Task.WhenAll(tasks);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected batch stat failure. CommandId: {CommandId}", command.CommandId);
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An unexpected error occurred while retrieving batch status.")
            };
        }

        var result = new BatchStatResult
        {
            Items = items,
            Succeeded = items.Count(item => item.Success),
            Failed = items.Count(item => !item.Success)
        };

        var dataJson = JsonSerializer.SerializeToElement(result, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);
        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };

        async Task ProcessPathAsync(string? path, int index)
        {
            var itemPath = path ?? string.Empty;
            await concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var statResult = await _fileSystemExecutor.StatAsync(
                        itemPath,
                        command.CommandId,
                        cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    var itemSucceeded = statResult.Success && statResult.Data is not null;
                    items[index] = new BatchStatItemResult
                    {
                        Path = itemPath,
                        Success = itemSucceeded,
                        Data = itemSucceeded ? statResult.Data : null,
                        Error = itemSucceeded
                            ? null
                            : statResult.Error ?? new CommandError(
                                ErrorCodes.InternalError,
                                "Path status did not return a result.")
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected batch stat item failure at index {Index}. CommandId: {CommandId}", index, command.CommandId);
                    items[index] = new BatchStatItemResult
                    {
                        Path = itemPath,
                        Success = false,
                        Error = new CommandError(ErrorCodes.InternalError, "An unexpected error occurred while retrieving path status.")
                    };
                }
            }
            finally
            {
                concurrencyGate.Release();
            }
        }
    }

    private async Task<CommandResult<JsonElement>> HandleBatchReadAsync(
        BatchReadCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Paths is null || command.Paths.Count < 1 || command.Paths.Count > 20)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InvalidRequest, "paths must contain between 1 and 20 entries.")
            };
        }

        if (command.MaxBytesPerFile < 1 || command.MaxBytesPerFile > 1_048_576)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InvalidRequest, "maxBytesPerFile must be between 1 and 1048576.")
            };
        }

        if (command.MaxTotalBytes < 1 || command.MaxTotalBytes > 8_388_608)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InvalidRequest, "maxTotalBytes must be between 1 and 8388608.")
            };
        }

        var items = new BatchReadItemResult[command.Paths.Count];
        using var concurrencyGate = new SemaphoreSlim(initialCount: 4, maxCount: 4);
        var budgetTurns = Enumerable.Range(0, command.Paths.Count + 1)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        budgetTurns[0].TrySetResult(true);
        long remainingTotalBytes = command.MaxTotalBytes;

        try
        {
            var tasks = command.Paths.Select((path, index) => ProcessPathAsync(path, index)).ToArray();
            await Task.WhenAll(tasks);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected batch read failure. CommandId: {CommandId}", command.CommandId);
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An unexpected error occurred while reading the batch.")
            };
        }

        var batchResult = new BatchReadResult
        {
            Items = items,
            Succeeded = items.Count(item => item.Success),
            Failed = items.Count(item => !item.Success),
            TotalBytesReturned = command.MaxTotalBytes - remainingTotalBytes
        };

        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(
                batchResult,
                LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default)
        };

        async Task ProcessPathAsync(string? path, int index)
        {
            var itemPath = path ?? string.Empty;
            BatchReadItemResult item;
            var gateEntered = false;

            try
            {
                await concurrencyGate.WaitAsync(cancellationToken);
                gateEntered = true;
                cancellationToken.ThrowIfCancellationRequested();

                var policyError = _pathPolicy.AuthorizeReadFile(itemPath, out var normalizedPath);
                if (policyError is not null)
                {
                    item = new BatchReadItemResult
                    {
                        Path = itemPath,
                        Success = false,
                        Error = policyError
                    };
                }
                else
                {
                    var readResult = await _fileSystemExecutor.ReadFileAsync(
                        normalizedPath,
                        command.CommandId,
                        cancellationToken);

                    if (!readResult.Success || readResult.Data is null)
                    {
                        item = new BatchReadItemResult
                        {
                            Path = itemPath,
                            Success = false,
                            Error = readResult.Error ?? new CommandError(
                                ErrorCodes.InternalError,
                                "The file read did not return a result.")
                        };
                    }
                    else
                    {
                        var fullContentBytes = Encoding.UTF8.GetByteCount(readResult.Data.Content);
                        var (content, bytesReturned) = TruncateUtf8(
                            readResult.Data.Content,
                            command.MaxBytesPerFile);

                        item = new BatchReadItemResult
                        {
                            Path = itemPath,
                            Success = true,
                            Data = new BatchReadFileResult
                            {
                                Path = readResult.Data.Path,
                                Content = content,
                                Encoding = readResult.Data.Encoding,
                                Size = readResult.Data.Size,
                                Sha256 = readResult.Data.Sha256,
                                BytesReturned = bytesReturned,
                                Truncated = bytesReturned < fullContentBytes
                            }
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                item = new BatchReadItemResult
                {
                    Path = itemPath,
                    Success = false,
                    Error = new CommandError(ErrorCodes.CommandCancelled, "The file read operation was cancelled.")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected batch read item failure at index {Index}. CommandId: {CommandId}", index, command.CommandId);
                item = new BatchReadItemResult
                {
                    Path = itemPath,
                    Success = false,
                    Error = new CommandError(ErrorCodes.InternalError, "An unexpected error occurred while reading the file.")
                };
            }
            finally
            {
                if (gateEntered)
                    concurrencyGate.Release();
            }

            await budgetTurns[index].Task;
            try
            {
                if (item.Success && item.Data is not null)
                {
                    var allowedBytes = (int)Math.Min(item.Data.BytesReturned, remainingTotalBytes);
                    if (allowedBytes < item.Data.BytesReturned)
                    {
                        var originalBytes = item.Data.BytesReturned;
                        var (content, bytesReturned) = TruncateUtf8(item.Data.Content, allowedBytes);
                        item = item with
                        {
                            Data = item.Data with
                            {
                                Content = content,
                                BytesReturned = bytesReturned,
                                Truncated = item.Data.Truncated || bytesReturned < originalBytes
                            }
                        };
                    }

                    remainingTotalBytes -= item.Data!.BytesReturned;
                }

                items[index] = item;
            }
            finally
            {
                budgetTurns[index + 1].TrySetResult(true);
            }
        }
    }

    private static (string Content, int BytesReturned) TruncateUtf8(string content, int maxBytes)
    {
        if (maxBytes <= 0 || content.Length == 0)
            return (string.Empty, 0);

        var builder = new StringBuilder(Math.Min(content.Length, maxBytes));
        var bytesReturned = 0;
        foreach (var rune in content.EnumerateRunes())
        {
            if (bytesReturned + rune.Utf8SequenceLength > maxBytes)
                break;

            builder.Append(rune.ToString());
            bytesReturned += rune.Utf8SequenceLength;
        }

        return (builder.ToString(), bytesReturned);
    }

    private async Task<CommandResult<JsonElement>> HandleMoveAsync(
        MoveCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _fileSystemExecutor.MoveAsync(
            command.Path,
            command.Destination,
            command.Overwrite,
            command.ExpectedSha256,
            command.CommandId,
            cancellationToken
        );

        if (!result.Success || result.Data == null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = result.Error
            };
        }

        var dataJson = JsonSerializer.SerializeToElement(result.Data, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);
        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };
    }

    private async Task<CommandResult<JsonElement>> HandleCopyAsync(
        CopyCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _directoryCopyExecutor.ExecuteAsync(
            command.Path,
            command.Destination,
            command.Overwrite,
            command.ExpectedSourceSha256,
            command.Recursive,
            command.MaxEntries,
            command.MaxTotalBytes,
            command.CommandId,
            cancellationToken
        );

        if (!result.Success || result.Data == null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = result.Error
            };
        }

        var dataJson = JsonSerializer.SerializeToElement(result.Data, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);
        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };
    }

    private sealed class FileCopyFallbackExecutor : IDirectoryCopyExecutor
    {
        private readonly IFileSystemExecutor _executor;

        public FileCopyFallbackExecutor(IFileSystemExecutor executor)
        {
            _executor = executor;
        }

        public Task<CommandResult<CopyResult>> ExecuteAsync(
            string sourcePath,
            string destinationPath,
            bool overwrite,
            string? expectedSourceSha256,
            bool recursive,
            int maxEntries,
            long maxTotalBytes,
            Guid commandId,
            CancellationToken cancellationToken)
        {
            if (Directory.Exists(sourcePath))
            {
                return Task.FromResult(new CommandResult<CopyResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.InvalidRequest, "Directory copy executor is unavailable.")
                });
            }

            _ = recursive;
            _ = maxEntries;

            return _executor.CopyAsync(
                sourcePath,
                destinationPath,
                overwrite,
                expectedSourceSha256,
                maxTotalBytes,
                commandId,
                cancellationToken);
        }
    }

    private async Task<CommandResult<JsonElement>> HandleRemoveDirectoryAsync(
        RemoveDirectoryCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _fileSystemExecutor.RemoveDirectoryAsync(
            command.Path,
            command.MissingOk,
            command.CommandId,
            cancellationToken
        );

        if (!result.Success || result.Data == null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = result.Error
            };
        }

        var dataJson = JsonSerializer.SerializeToElement(result.Data, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);
        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };
    }

    private async Task<CommandResult<JsonElement>> HandleDeleteAsync(
        DeleteCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _fileSystemExecutor.DeleteAsync(
            command.Path,
            command.ExpectedSha256,
            command.MissingOk,
            command.CommandId,
            cancellationToken
        );

        if (!result.Success || result.Data == null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = result.Error
            };
        }

        var dataJson = JsonSerializer.SerializeToElement(result.Data, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);
        return new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = dataJson
        };
    }
}

// ── PowerShell session handlers ────────────────────────────────────────────────

public sealed partial class CommandHandler
{
    private static readonly string[] SessionStateStrings =
    [
        "running",    // PowerShellSessionStateValue.Running   = 0
        "completed",  // PowerShellSessionStateValue.Completed = 1
        "failed",     // PowerShellSessionStateValue.Failed    = 2
        "cancelled",  // PowerShellSessionStateValue.Cancelled = 3
        "timedOut"    // PowerShellSessionStateValue.TimedOut  = 4
    ];

    private Task<CommandResult<JsonElement>> HandlePowerShellStartAsync(
        PowerShellStartCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_sessionRegistry is null || _sessionExecutor is null)
        {
            return Task.FromResult(SessionError(command.CommandId, ErrorCodes.InternalError,
                "PowerShell session infrastructure is not available."));
        }

        // ── Path policy ────────────────────────────────────────────────────
        var error = _pathPolicy.AuthorizeCreateDirectory(
            command.WorkingDirectory,
            out var normalizedPath,
            recursive: false);
        if (error is not null)
            return Task.FromResult(SessionError(command.CommandId, error.Code, error.Message));

        if (!Directory.Exists(normalizedPath))
            return Task.FromResult(SessionError(command.CommandId, ErrorCodes.DirectoryNotFound,
                "The PowerShell working directory was not found."));

        // ── Guardrails (same as powershell_exec) ──────────────────────────
        if (string.IsNullOrWhiteSpace(command.Script) ||
            command.Script.Length > 65_536 ||
            command.Script.Contains('\0'))
        {
            return Task.FromResult(SessionError(command.CommandId, ErrorCodes.InvalidRequest,
                "script must be non-empty, contain no NUL characters, and be at most 65536 characters."));
        }

        if (command.TimeoutSeconds is < 1 or > 900)
            return Task.FromResult(SessionError(command.CommandId, ErrorCodes.InvalidRequest,
                "timeoutSeconds must be between 1 and 900."));

        if (command.MaxOutputBytes is < 1024 or > 4_194_304)
            return Task.FromResult(SessionError(command.CommandId, ErrorCodes.InvalidRequest,
                "maxOutputBytes must be between 1024 and 4194304."));

        if (command.Elevated && !command.Visible)
            return Task.FromResult(SessionError(command.CommandId, ErrorCodes.InvalidRequest,
                "elevated=true is only supported for visible PowerShell execution."));

        // Hidden sessions cannot accept interactive input — elevated not supported
        if (command.Elevated)
            return Task.FromResult(SessionError(command.CommandId, ErrorCodes.InvalidRequest,
                "elevated is not supported for async PowerShell sessions."));

        if (FileSystem.FileSystemExecutor.IsCurrentProcessElevated())
            return Task.FromResult(SessionError(command.CommandId, ErrorCodes.AccessDenied,
                "PowerShell execution is disabled while the Windows agent is running elevated."));

        var executable = FileSystem.FileSystemExecutor.ResolveToolExecutable("pwsh.exe", normalizedPath);
        if (executable is null)
            return Task.FromResult(SessionError(command.CommandId, ErrorCodes.InvalidRequest,
                "PowerShell 7 (pwsh.exe) is not available on the Windows agent."));

        // ── Session registry ───────────────────────────────────────────────
        var session = _sessionRegistry.TryCreate(
            command.DeviceId,
            command.MaxOutputBytes,
            out var registryError);

        if (session is null)
            return Task.FromResult(SessionError(command.CommandId, ErrorCodes.InvalidRequest, registryError!));

        // Check cancellationToken again before starting the background process
        if (cancellationToken.IsCancellationRequested)
        {
            session.Dispose();
            throw new OperationCanceledException(cancellationToken);
        }

        // ── Launch async (returns immediately) ────────────────────────────
        _sessionExecutor.StartBackground(
            session,
            executable,
            normalizedPath,
            command.Script,
            command.TimeoutSeconds);

        var result = new PowerShellStartResult
        {
            SessionId = session.SessionId,
            State = "running",
            StartedAt = session.StartedAt
        };

        return Task.FromResult(new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(result, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default)
        });
    }

    private Task<CommandResult<JsonElement>> HandlePowerShellStatusAsync(
        PowerShellStatusCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_sessionRegistry is null)
            return Task.FromResult(SessionError(command.CommandId, ErrorCodes.InternalError,
                "PowerShell session infrastructure is not available."));

        var session = _sessionRegistry.Get(command.SessionId);
        if (session is null)
            return Task.FromResult(SessionError(command.CommandId, "SESSION_NOT_FOUND",
                $"Session {command.SessionId} was not found."));

        // Enforce device isolation: caller must own this session
        if (!string.Equals(session.DeviceId, command.DeviceId, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(SessionError(command.CommandId, "SESSION_NOT_FOUND",
                $"Session {command.SessionId} was not found."));

        if (command.StdoutOffset < 0 || command.StderrOffset < 0)
            return Task.FromResult(SessionError(command.CommandId, ErrorCodes.InvalidRequest,
                "stdoutOffset and stderrOffset must be >= 0."));

        if (command.MaxOutputBytes is < 1 or > 262_144)
            return Task.FromResult(SessionError(command.CommandId, ErrorCodes.InvalidRequest,
                "maxOutputBytes must be between 1 and 262144."));

        var snapshot = session.ReadOutput(command.StdoutOffset, command.StderrOffset, command.MaxOutputBytes);
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        var result = new PowerShellSessionResult
        {
            SessionId = session.SessionId,
            State = SessionStateStrings[(int)session.State],
            StartedAt = session.StartedAt,
            CompletedAt = session.CompletedAt,
            ExitCode = session.ExitCode,
            Stdout = utf8.GetString(snapshot.StdoutBytes),
            Stderr = utf8.GetString(snapshot.StderrBytes),
            NextStdoutOffset = snapshot.NextStdoutOffset,
            NextStderrOffset = snapshot.NextStderrOffset,
            Truncated = snapshot.Truncated
        };

        return Task.FromResult(new CommandResult<JsonElement>
        {
            CommandId = command.CommandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(result, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default)
        });
    }

    private async Task<CommandResult<JsonElement>> HandlePowerShellCancelAsync(
        PowerShellCancelCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_sessionRegistry is null)
            return SessionError(command.CommandId, ErrorCodes.InternalError,
                "PowerShell session infrastructure is not available.");

        var session = _sessionRegistry.Get(command.SessionId);
        if (session is null)
            return SessionError(command.CommandId, "SESSION_NOT_FOUND",
                $"Session {command.SessionId} was not found.");

        // Enforce device isolation
        if (!string.Equals(session.DeviceId, command.DeviceId, StringComparison.OrdinalIgnoreCase))
            return SessionError(command.CommandId, "SESSION_NOT_FOUND",
                $"Session {command.SessionId} was not found.");

        // If running, cancel it
        if (session.State == PowerShellSessionStateValue.Running)
        {
            _sessionRegistry.Cancel(session);

            // Bounded wait suitable for AgentCommandTimeouts (up to 5s)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await session.CompletionTask.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout elapsed, just return the current state
            }
        }

        return BuildSessionResult(command.CommandId, session);
    }

    private CommandResult<JsonElement> BuildSessionResult(Guid commandId, PowerShellSessionState session)
    {
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        var snapshot = session.ReadOutput(0, 0, 262_144);
        var result = new PowerShellSessionResult
        {
            SessionId = session.SessionId,
            State = SessionStateStrings[(int)session.State],
            StartedAt = session.StartedAt,
            CompletedAt = session.CompletedAt,
            ExitCode = session.ExitCode,
            Stdout = utf8.GetString(snapshot.StdoutBytes),
            Stderr = utf8.GetString(snapshot.StderrBytes),
            NextStdoutOffset = snapshot.NextStdoutOffset,
            NextStderrOffset = snapshot.NextStderrOffset,
            Truncated = snapshot.Truncated
        };

        return new CommandResult<JsonElement>
        {
            CommandId = commandId,
            Success = true,
            Data = JsonSerializer.SerializeToElement(result, LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default)
        };
    }

    private static CommandResult<JsonElement> SessionError(Guid commandId, string code, string message) =>
        new()
        {
            CommandId = commandId,
            Success = false,
            Error = new LocalMcp.Contracts.Results.CommandError(code, message),
            Data = JsonSerializer.SerializeToElement<object?>(null)
        };
}

