using LocalMcp.Contracts.Results;
using LocalMcp.Contracts.Commands;

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
        int maxEntries,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<SearchFilesResult>> SearchFilesAsync(
        string path,
        string query,
        int maxResults,
        int maxDepth,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<WriteFileResult>> WriteFileAsync(
        string path,
        string content,
        string? expectedSha256,
        bool createIfMissing,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<PatchFileResult>> PatchFileAsync(
        string path,
        string expectedSha256,
        List<PatchEdit> edits,
        Guid commandId,
        CancellationToken cancellationToken
    );
}
