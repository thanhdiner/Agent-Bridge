using System.Text.Json;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Logging;

namespace LocalMcp.Agent.Windows.Commands;

public sealed class CommandHandler
{
    private readonly IPathPolicy _pathPolicy;
    private readonly IFileSystemExecutor _fileSystemExecutor;
    private readonly ILogger<CommandHandler> _logger;

    public CommandHandler(
        IPathPolicy pathPolicy,
        IFileSystemExecutor fileSystemExecutor,
        ILogger<CommandHandler> logger)
    {
        _pathPolicy = pathPolicy;
        _fileSystemExecutor = fileSystemExecutor;
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
        else if (command is ListDirectoryCommand listDirectoryCommand)
        {
            return await HandleListDirectoryAsync(listDirectoryCommand, cancellationToken);
        }
        else if (command is SearchFilesCommand searchFilesCommand)
        {
            return await HandleSearchFilesAsync(searchFilesCommand, cancellationToken);
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
        else if (command is CreateDirectoryCommand createDirectoryCommand)
        {
            return await HandleCreateDirectoryAsync(createDirectoryCommand, cancellationToken);
        }
        else if (command is StatCommand statCommand)
        {
            return await HandleStatAsync(statCommand, cancellationToken);
        }
        else if (command is MoveCommand moveCommand)
        {
            return await HandleMoveAsync(moveCommand, cancellationToken);
        }
        else if (command is CopyCommand copyCommand)
        {
            return await HandleCopyAsync(copyCommand, cancellationToken);
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
        var result = await _fileSystemExecutor.CopyAsync(
            command.Path,
            command.Destination,
            command.Overwrite,
            command.ExpectedSourceSha256,
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
