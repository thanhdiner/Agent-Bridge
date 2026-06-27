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
        // 1. Validate path policy (sandbox check)
        var error = _pathPolicy.Validate(command.Path, out var normalizedPath, isDirectory: true);
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

        // 2. Execute tree
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
        // 1. Validate path policy (sandbox check)
        var error = _pathPolicy.Validate(command.Path, out var normalizedPath, isDirectory: true);
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

        // 2. Execute list
        var listResult = await _fileSystemExecutor.ListDirectoryAsync(normalizedPath, command.IncludeHidden, command.CommandId, cancellationToken);

        if (!listResult.Success || listResult.Data == null)
        {
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = listResult.Error
            };
        }

        // 3. Filter children using the same PathPolicy sandbox rules
        var filteredDirs = new List<DirectoryEntry>();
        foreach (var dir in listResult.Data.Directories)
        {
            var itemError = _pathPolicy.Validate(dir.Path, out _, isDirectory: true);
            if (itemError is null)
            {
                filteredDirs.Add(dir);
            }
        }

        var filteredFiles = new List<FileEntry>();
        foreach (var file in listResult.Data.Files)
        {
            var itemError = _pathPolicy.Validate(file.Path, out _, isDirectory: false);
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
        // 1. Validate path policy (sandbox check)
        var error = _pathPolicy.Validate(command.Path, out var normalizedPath, isDirectory: true);
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

        // 2. Execute search
        var searchResult = await _fileSystemExecutor.SearchFilesAsync(
            normalizedPath,
            command.Query,
            command.Mode,
            command.FilePattern,
            command.CaseSensitive,
            command.MaxResults,
            command.MaxFileBytes,
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

        // 3. Filter matches using the same PathPolicy sandbox rules
        var filteredMatches = new List<SearchMatch>();
        foreach (var item in searchResult.Data.Matches)
        {
            // We search files, so isDirectory is false
            var itemError = _pathPolicy.Validate(item.FullPath, out _, isDirectory: false);
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
        // 1. Validate path policy (sandbox check)
        var error = _pathPolicy.Validate(command.Path, out var normalizedPath);
        if (error is not null)
        {
            _logger.LogWarning("Path validation failed for command {CommandId}: {ErrorCode} - {ErrorMessage}", command.CommandId, error.Code, error.Message);
            return new CommandResult<JsonElement>
            {
                CommandId = command.CommandId,
                Success = false,
                Error = error,
                Data = JsonSerializer.SerializeToElement<object?>(null)
            };
        }

        // 2. Execute read
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
}
