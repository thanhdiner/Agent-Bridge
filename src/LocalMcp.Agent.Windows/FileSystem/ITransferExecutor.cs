using System.Security.Cryptography;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalMcp.Agent.Windows.FileSystem;

public interface ITransferExecutor
{
    Task<CommandResult<CopyResult>> ExecuteAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        string? expectedSourceSha256,
        bool recursive,
        int maxEntries,
        long maxTotalBytes,
        Guid commandId,
        CancellationToken cancellationToken);
}

public interface IDirectoryCopyExecutor : ITransferExecutor
{
}

public sealed class DirectoryCopyExecutor : IDirectoryCopyExecutor
{
    private readonly IFileSystemExecutor _fileExecutor;
    private readonly IPathPolicy _pathPolicy;
    private readonly FileAccessOptions _options;
    private readonly ILogger<DirectoryCopyExecutor> _logger;

    internal Action<string>? OnFileCopiedHook { get; set; }

    public DirectoryCopyExecutor(
        IFileSystemExecutor fileExecutor,
        IPathPolicy pathPolicy,
        IOptions<FileAccessOptions> options,
        ILogger<DirectoryCopyExecutor> logger)
    {
        _fileExecutor = fileExecutor;
        _pathPolicy = pathPolicy;
        _options = options.Value;
        _logger = logger;
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
        return ExecuteCoreAsync(
            sourcePath,
            destinationPath,
            overwrite,
            expectedSourceSha256,
            recursive,
            maxEntries,
            maxTotalBytes,
            commandId,
            cancellationToken);
    }

    private async Task<CommandResult<CopyResult>> ExecuteCoreAsync(
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
        if (maxEntries < 1 || maxEntries > 5000)
            return Failure(commandId, ErrorCodes.InvalidRequest, "maxEntries must be between 1 and 5000.");

        if (maxTotalBytes < 1 || maxTotalBytes > 1073741824)
            return Failure(commandId, ErrorCodes.InvalidRequest, "maxTotalBytes must be between 1 and 1073741824.");

        if (cancellationToken.IsCancellationRequested)
            return Failure(commandId, ErrorCodes.CommandCancelled, "The command was cancelled.");

        if (!Directory.Exists(sourcePath))
        {
            var filePolicyError = _pathPolicy.AuthorizeCopy(
                sourcePath,
                destinationPath,
                overwrite,
                out var physicalSource,
                out var physicalDestination);
            if (filePolicyError is not null)
                return new CommandResult<CopyResult> { CommandId = commandId, Success = false, Error = filePolicyError };

            if (new FileInfo(physicalSource).Length > maxTotalBytes)
                return Failure(commandId, ErrorCodes.FileTooLarge, "The source file exceeds maxTotalBytes.");

            var result = await _fileExecutor.CopyAsync(
                physicalSource,
                physicalDestination,
                overwrite,
                expectedSourceSha256,
                maxTotalBytes,
                commandId,
                cancellationToken);

            if (!result.Success || result.Data is null)
                return result;

            return new CommandResult<CopyResult>
            {
                CommandId = commandId,
                Success = true,
                Data = result.Data with
                {
                    IsDirectory = false,
                    FilesCopied = 1,
                    DirectoriesCreated = 0
                }
            };
        }

        if (!recursive)
            return Failure(commandId, ErrorCodes.InvalidRequest, "Directory sources require recursive=true.");

        if (overwrite)
            return Failure(commandId, ErrorCodes.InvalidRequest, "Directory copy does not support overwrite or merge.");

        if (!string.IsNullOrWhiteSpace(expectedSourceSha256))
            return Failure(commandId, ErrorCodes.InvalidRequest, "expectedSourceSha256 is supported only for file sources.");

        return await CopyDirectoryAsync(
            sourcePath,
            destinationPath,
            maxEntries,
            maxTotalBytes,
            commandId,
            cancellationToken);
    }

    private async Task<CommandResult<CopyResult>> CopyDirectoryAsync(
        string sourcePath,
        string destinationPath,
        int maxEntries,
        long maxTotalBytes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var policyError = _pathPolicy.AuthorizeCopy(
            sourcePath,
            destinationPath,
            overwrite: false,
            out var physicalSource,
            out var physicalDestination);
        if (policyError is not null)
            return new CommandResult<CopyResult> { CommandId = commandId, Success = false, Error = policyError };

        try
        {
            return await CopyDirectoryCoreAsync(
                physicalSource,
                physicalDestination,
                maxEntries,
                maxTotalBytes,
                commandId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure(commandId, ErrorCodes.CommandCancelled, "The command was cancelled.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied during directory copy. CommandId: {CommandId}", commandId);
            return Failure(commandId, ErrorCodes.AccessDenied, "Access was denied while copying the directory.");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "IO error during directory copy. CommandId: {CommandId}", commandId);
            return Failure(commandId, ErrorCodes.WriteError, "Failed to copy the directory due to an IO error.");
        }
        catch (OverflowException ex)
        {
            _logger.LogWarning(ex, "Directory byte count overflow. CommandId: {CommandId}", commandId);
            return Failure(commandId, ErrorCodes.FileTooLarge, "The directory size exceeds the supported limit.");
        }
    }

    private async Task<CommandResult<CopyResult>> CopyDirectoryCoreAsync(
        string sourcePath,
        string destinationPath,
        int maxEntries,
        long maxTotalBytes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var initialPlanResult = CreatePlan(sourcePath, destinationPath, maxEntries, maxTotalBytes, cancellationToken);
        if (initialPlanResult.Error is not null)
            return new CommandResult<CopyResult> { CommandId = commandId, Success = false, Error = initialPlanResult.Error };

        var plan = initialPlanResult.Plan!;
        var destinationParent = Path.GetDirectoryName(destinationPath)!;
        string? temporaryPath = Path.Combine(destinationParent, $".copy-temp-{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(temporaryPath);
            var temporaryError = VerifyTemporaryDirectory(temporaryPath);
            if (temporaryError is not null)
                return new CommandResult<CopyResult> { CommandId = commandId, Success = false, Error = temporaryError };

            var copyError = await CopyPlanAsync(plan, temporaryPath, cancellationToken);
            if (copyError is not null)
                return new CommandResult<CopyResult> { CommandId = commandId, Success = false, Error = copyError };

            var finalPlanResult = CreatePlan(sourcePath, destinationPath, maxEntries, maxTotalBytes, cancellationToken);
            if (finalPlanResult.Error is not null || !PlansMatch(plan, finalPlanResult.Plan!))
                return Failure(commandId, ErrorCodes.ConcurrencyConflict, "The source directory changed during the copy operation.");

            var hashError = await VerifySourceHashesAsync(plan, cancellationToken);
            if (hashError is not null)
                return new CommandResult<CopyResult> { CommandId = commandId, Success = false, Error = hashError };

            var finalPolicyError = _pathPolicy.AuthorizeCopy(
                sourcePath,
                destinationPath,
                overwrite: false,
                out var finalSource,
                out var finalDestination);
            if (finalPolicyError is not null)
                return new CommandResult<CopyResult> { CommandId = commandId, Success = false, Error = finalPolicyError };

            if (!string.Equals(sourcePath, finalSource, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(destinationPath, finalDestination, StringComparison.OrdinalIgnoreCase))
                return Failure(commandId, ErrorCodes.ConcurrencyConflict, "The source or destination path changed during validation.");

            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                return Failure(commandId, ErrorCodes.FileAlreadyExists, "The destination path appeared concurrently.");

            var finalTemporaryError = VerifyTemporaryDirectory(temporaryPath);
            if (finalTemporaryError is not null)
                return new CommandResult<CopyResult> { CommandId = commandId, Success = false, Error = finalTemporaryError };

            Directory.Move(temporaryPath, destinationPath);
            temporaryPath = null;

            return new CommandResult<CopyResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new CopyResult
                {
                    Path = destinationPath,
                    IsDirectory = true,
                    FilesCopied = plan.Files.Count,
                    DirectoriesCreated = plan.Directories.Count + 1,
                    BytesCopied = plan.TotalBytes,
                    Sha256 = null,
                    LastWriteTimeUtc = new DirectoryInfo(destinationPath).LastWriteTimeUtc
                }
            };
        }
        finally
        {
            CleanupTemporaryDirectory(temporaryPath, destinationParent);
        }
    }

    private async Task<CommandError?> CopyPlanAsync(
        DirectoryPlan plan,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        foreach (var directory in plan.Directories
                     .OrderBy(item => item.Depth)
                     .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(temporaryPath, directory.RelativePath));
        }

        foreach (var file in plan.Files.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = Path.Combine(temporaryPath, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            using var sourceStream = new FileStream(
                file.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var destinationStream = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[81920];
            int read;
            long copiedLength = 0;
            while ((read = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                copiedLength += read;
                await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
            }

            destinationStream.Flush(flushToDisk: true);
            file.CopiedSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (copiedLength != file.Length)
                return new CommandError(ErrorCodes.ConcurrencyConflict, "A source file changed during the copy operation.");

            OnFileCopiedHook?.Invoke(file.SourcePath);
        }

        return null;
    }

    private static async Task<CommandError?> VerifySourceHashesAsync(
        DirectoryPlan plan,
        CancellationToken cancellationToken)
    {
        foreach (var file in plan.Files)
        {
            using var stream = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(hash, file.CopiedSha256, StringComparison.Ordinal))
                return new CommandError(ErrorCodes.ConcurrencyConflict, "A source file changed during the copy operation.");
        }

        return null;
    }

    private (DirectoryPlan? Plan, CommandError? Error) CreatePlan(
        string sourcePath,
        string destinationPath,
        int maxEntries,
        long maxTotalBytes,
        CancellationToken cancellationToken)
    {
        var root = new DirectoryInfo(sourcePath);
        if (!root.Exists)
            return (null, new CommandError(ErrorCodes.DirectoryNotFound, "The source directory was not found."));
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return (null, new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed in directory copies."));

        var plan = new DirectoryPlan();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        var entryCount = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            foreach (var entry in current.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryCount++;
                if (entryCount > maxEntries)
                    return (null, new CommandError(ErrorCodes.ResultLimitExceeded, "The directory contains more entries than maxEntries."));

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return (null, new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed in directory copies."));

                var policyError = _pathPolicy.AuthorizeStat(entry.FullName, out var normalizedEntry);
                if (policyError is not null)
                    return (null, policyError);

                var relativePath = Path.GetRelativePath(sourcePath, normalizedEntry);
                if (string.IsNullOrWhiteSpace(relativePath) ||
                    relativePath == "." ||
                    Path.IsPathRooted(relativePath) ||
                    relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return (null, new CommandError(ErrorCodes.AccessDenied, "A source entry escaped the source directory."));

                var destinationEntry = Path.GetFullPath(Path.Combine(destinationPath, relativePath));
                if (!PathPolicy.IsSubdirectoryOf(destinationEntry, destinationPath))
                    return (null, new CommandError(ErrorCodes.AccessDenied, "A destination entry escaped the destination directory."));

                var destinationError = ValidateDestinationEntry(relativePath, entry is DirectoryInfo);
                if (destinationError is not null)
                    return (null, destinationError);

                if (entry is DirectoryInfo directory)
                {
                    plan.Directories.Add(new DirectoryItem(relativePath, GetRelativeDepth(relativePath)));
                    pending.Push(directory);
                }
                else if (entry is FileInfo file)
                {
                    checked { plan.TotalBytes += file.Length; }
                    if (plan.TotalBytes > maxTotalBytes)
                        return (null, new CommandError(ErrorCodes.FileTooLarge, "The directory exceeds maxTotalBytes."));

                    plan.Files.Add(new FileItem(normalizedEntry, relativePath, file.Length, file.LastWriteTimeUtc));
                }
            }
        }

        return (plan, null);
    }

    private CommandError? ValidateDestinationEntry(string relativePath, bool isDirectory)
    {
        var segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (_options.DeniedSegments.Any(item => string.Equals(item, segment, StringComparison.OrdinalIgnoreCase)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to destination segment '{segment}'.");
        }

        var name = segments.LastOrDefault();
        if (string.IsNullOrEmpty(name))
            return null;

        if (_options.DeniedFileNames.Any(item => PathPolicy.MatchFileName(name, item)) ||
            _options.DeniedWriteFileNames.Any(item => PathPolicy.MatchFileName(name, item)))
            return new CommandError(ErrorCodes.AccessDenied, $"Access denied to destination entry '{name}'.");

        if (!isDirectory)
        {
            var extension = Path.GetExtension(name);
            if (!string.IsNullOrEmpty(extension) &&
                _options.DeniedWriteExtensions.Any(item => string.Equals(item, extension, StringComparison.OrdinalIgnoreCase)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to destination extension '{extension}'.");
        }

        return null;
    }

    private static int GetRelativeDepth(string relativePath)
    {
        return relativePath.Count(character =>
            character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar) + 1;
    }

    private static bool PlansMatch(DirectoryPlan first, DirectoryPlan second)
    {
        if (first.TotalBytes != second.TotalBytes ||
            first.Directories.Count != second.Directories.Count ||
            first.Files.Count != second.Files.Count)
            return false;

        var firstDirectories = first.Directories
            .Select(item => item.RelativePath)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase);
        var secondDirectories = second.Directories
            .Select(item => item.RelativePath)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase);
        if (!firstDirectories.SequenceEqual(secondDirectories, StringComparer.OrdinalIgnoreCase))
            return false;

        var firstFiles = first.Files.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        var secondFiles = second.Files.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        for (var index = 0; index < firstFiles.Count; index++)
        {
            if (!string.Equals(firstFiles[index].RelativePath, secondFiles[index].RelativePath, StringComparison.OrdinalIgnoreCase) ||
                firstFiles[index].Length != secondFiles[index].Length ||
                firstFiles[index].LastWriteTimeUtc != secondFiles[index].LastWriteTimeUtc)
                return false;
        }

        return true;
    }

    private CommandError? VerifyTemporaryDirectory(string temporaryPath)
    {
        var error = _pathPolicy.AuthorizeStat(temporaryPath, out var normalizedPath);
        if (error is not null)
            return error;

        if (!string.Equals(temporaryPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            return new CommandError(ErrorCodes.AccessDenied, "Temporary directory resolved to an unexpected path.");

        var attributes = File.GetAttributes(normalizedPath);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed for temporary directories.");

        return null;
    }

    private static void CleanupTemporaryDirectory(string? temporaryPath, string destinationParent)
    {
        if (string.IsNullOrWhiteSpace(temporaryPath))
            return;

        try
        {
            var fullTemporaryPath = Path.GetFullPath(temporaryPath);
            if (!PathPolicy.IsSubdirectoryOf(fullTemporaryPath, destinationParent) || !Directory.Exists(fullTemporaryPath))
                return;

            var attributes = File.GetAttributes(fullTemporaryPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                return;

            DeleteTreeWithoutFollowingReparsePoints(fullTemporaryPath);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void DeleteTreeWithoutFollowingReparsePoints(string directoryPath)
    {
        foreach (var entry in new DirectoryInfo(directoryPath).EnumerateFileSystemInfos())
        {
            var isReparsePoint = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (entry is DirectoryInfo directory)
            {
                if (isReparsePoint)
                {
                    Directory.Delete(directory.FullName);
                }
                else
                {
                    DeleteTreeWithoutFollowingReparsePoints(directory.FullName);
                }
            }
            else
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                File.Delete(entry.FullName);
            }
        }

        Directory.Delete(directoryPath);
    }

    private sealed class DirectoryPlan
    {
        public List<DirectoryItem> Directories { get; } = new();
        public List<FileItem> Files { get; } = new();
        public long TotalBytes { get; set; }
    }

    private sealed record DirectoryItem(string RelativePath, int Depth);

    private sealed class FileItem
    {
        public FileItem(string sourcePath, string relativePath, long length, DateTime lastWriteTimeUtc)
        {
            SourcePath = sourcePath;
            RelativePath = relativePath;
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        public string SourcePath { get; }
        public string RelativePath { get; }
        public long Length { get; }
        public DateTime LastWriteTimeUtc { get; }
        public string? CopiedSha256 { get; set; }
    }

    private static CommandResult<CopyResult> Failure(Guid commandId, string code, string message)
    {
        return new CommandResult<CopyResult>
        {
            CommandId = commandId,
            Success = false,
            Error = new CommandError(code, message)
        };
    }
}
