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

    Task<CommandResult<ReadRangeResult>> ReadRangeAsync(
        string path,
        long startLine,
        int lineCount,
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

    Task<CommandResult<SearchContextResult>> SearchContextAsync(
        string path,
        string query,
        bool useRegex,
        bool caseSensitive,
        IReadOnlyList<string> includeGlobs,
        IReadOnlyList<string> excludeGlobs,
        int contextBefore,
        int contextAfter,
        int maxResults,
        int maxDepth,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<GitStatusResult>> GitStatusAsync(
        string path,
        bool includeUntracked,
        int maxEntries,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<GitDiffResult>> GitDiffAsync(
        string path,
        bool staged,
        bool includeUntracked,
        IReadOnlyList<string> pathSpecs,
        int contextLines,
        int maxBytes,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<GitLogResult>> GitLogAsync(
        string path,
        int maxCount,
        int skip,
        string? pathSpec,
        string? author,
        string? since,
        string? until,
        bool includeStats,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<GitShowResult>> GitShowAsync(
        string path,
        string revision,
        IReadOnlyList<string> pathSpecs,
        bool includePatch,
        bool includeStats,
        int contextLines,
        int maxBytes,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<ProjectVerifyResult>> ProjectCheckAsync(
        string path,
        string projectType,
        IReadOnlyList<string> steps,
        string configuration,
        int timeoutSeconds,
        int maxOutputBytes,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<PowerShellExecuteResult>> PowerShellExecuteAsync(
        string workingDirectory,
        string script,
        bool visible,
        bool elevated,
        int timeoutSeconds,
        int maxOutputBytes,
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

    Task<CommandResult<CreateDirectoryResult>> CreateDirectoryAsync(
        string path,
        bool recursive,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<StatResult>> StatAsync(
        string path,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<MoveResult>> MoveAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        string? expectedSha256,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<CopyResult>> CopyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        string? expectedSourceSha256,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<CopyResult>> CopyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        string? expectedSourceSha256,
        long maxTotalBytes,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<DeleteResult>> DeleteAsync(
        string path,
        string? expectedSha256,
        bool missingOk,
        Guid commandId,
        CancellationToken cancellationToken
    );

    Task<CommandResult<RemoveDirectoryResult>> RemoveDirectoryAsync(
        string path,
        bool missingOk,
        Guid commandId,
        CancellationToken cancellationToken
    );
}
