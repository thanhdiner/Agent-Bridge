using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.FileSystem;

public interface IFileSystemExecutor
{
    Task<CommandResult<ReadFileResult>> ReadFileAsync(
        string path,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<TreeResult>> GetTreeAsync(
        string path,
        int maxDepth,
        int maxEntries,
        bool includeHidden,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<ListDirectoryResult>> ListDirectoryAsync(
        string path,
        bool includeHidden,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<SearchFilesResult>> SearchFilesAsync(
        string path,
        string query,
        string mode,
        string? filePattern,
        bool caseSensitive,
        int maxResults,
        long maxFileBytes,
        Guid commandId,
        CancellationToken cancellationToken
    );
}
