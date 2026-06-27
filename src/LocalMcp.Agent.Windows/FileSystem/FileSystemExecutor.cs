using System.IO;
using System.Security.Cryptography;
using System.Text;
using LocalMcp.Contracts.Results;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Logging;
using LocalMcp.Agent.Windows.Security;

namespace LocalMcp.Agent.Windows.FileSystem;

public sealed class FileSystemExecutor : IFileSystemExecutor
{
    private readonly IPathPolicy _pathPolicy;
    private readonly ILogger<FileSystemExecutor> _logger;

    public FileSystemExecutor(IPathPolicy pathPolicy, ILogger<FileSystemExecutor> logger)
    {
        _pathPolicy = pathPolicy;
        _logger = logger;
    }

    public async Task<CommandResult<ReadFileResult>> ReadFileAsync(
        string path,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Reading file {Path} for command {CommandId}", path, commandId);

        try
        {
            var fileInfo = new FileInfo(path);
            var size = fileInfo.Length;

            // 1. Asynchronously read all bytes using FileStream
            byte[] bytes;
            using (var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true))
            {
                bytes = new byte[size];
                int totalBytesRead = 0;
                while (totalBytesRead < size)
                {
                    int bytesRead = await fs.ReadAsync(
                        bytes.AsMemory(totalBytesRead, (int)size - totalBytesRead),
                        cancellationToken
                    );
                    if (bytesRead == 0)
                    {
                        break;
                    }
                    totalBytesRead += bytesRead;
                }
            }

            // 2. Compute SHA-256
            var sha256Hash = ComputeSha256(bytes);

            // 3. Binary detection (check for null bytes)
            if (IsBinary(bytes))
            {
                _logger.LogWarning("File {Path} is detected as binary. Rejecting.", path);
                return new CommandResult<ReadFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.BinaryFileNotSupported, "Binary files are not supported.")
                };
            }

            // 4. Handle UTF-8 and UTF-8 BOM
            var (content, encoding) = DecodeText(bytes);

            return new CommandResult<ReadFileResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new ReadFileResult
                {
                    Path = path,
                    Content = content,
                    Encoding = encoding,
                    Size = size,
                    Sha256 = sha256Hash
                }
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("File read was cancelled for command {CommandId}", commandId);
            return new CommandResult<ReadFileResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The file read operation was cancelled.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read file {Path} for command {CommandId}", path, commandId);
            return new CommandResult<ReadFileResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An error occurred while reading the file.")
            };
        }
    }

    public Task<CommandResult<TreeResult>> GetTreeAsync(
        string path,
        int maxDepth,
        int maxEntries,
        bool includeHidden,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Building directory tree for {Path} (maxDepth={MaxDepth}, maxEntries={MaxEntries}, includeHidden={IncludeHidden})", path, maxDepth, maxEntries, includeHidden);

        try
        {
            var dirInfo = new DirectoryInfo(path);
            if (!dirInfo.Exists)
            {
                return Task.FromResult(new CommandResult<TreeResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.DirectoryNotFound, "The target directory was not found.")
                });
            }

            var entries = new List<TreeEntry>();
            var truncated = false;

            BuildTreeRecursive(dirInfo, path, 1, maxDepth, maxEntries, includeHidden, entries, ref truncated, cancellationToken);

            return Task.FromResult(new CommandResult<TreeResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new TreeResult
                {
                    Path = path,
                    Entries = entries,
                    Truncated = truncated
                }
            });
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(new CommandResult<TreeResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The tree traversal operation was cancelled.")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tree for path {Path}", path);
            return Task.FromResult(new CommandResult<TreeResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An error occurred while building the tree.")
            });
        }
    }

    private void BuildTreeRecursive(
        DirectoryInfo currentDir,
        string rootPath,
        int currentDepth,
        int maxDepth,
        int maxEntries,
        bool includeHidden,
        List<TreeEntry> entries,
        ref bool truncated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (truncated || entries.Count >= maxEntries)
        {
            truncated = true;
            return;
        }

        if (currentDepth > maxDepth)
        {
            return;
        }

        try
        {
            foreach (var info in currentDir.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entries.Count >= maxEntries)
                {
                    truncated = true;
                    return;
                }

                // 1. Skip reparse points (symlinks, junctions)
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                // 2. Skip hidden if not includeHidden
                if (!includeHidden && info.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    continue;
                }

                var isDir = info is DirectoryInfo;

                // 3. Centralized PathPolicy validation
                var error = _pathPolicy.Validate(info.FullName, out var normalizedPath, isDir);
                if (error is not null)
                {
                    continue;
                }

                long size = 0;
                if (!isDir && info is FileInfo fileInfo)
                {
                    size = fileInfo.Length;
                }

                var relativePath = Path.GetRelativePath(rootPath, normalizedPath);

                entries.Add(new TreeEntry
                {
                    Name = info.Name,
                    RelativePath = relativePath,
                    Type = isDir ? "directory" : "file",
                    Depth = currentDepth,
                    SizeBytes = size
                });

                if (isDir && info is DirectoryInfo subDir)
                {
                    BuildTreeRecursive(
                        subDir,
                        rootPath,
                        currentDepth + 1,
                        maxDepth,
                        maxEntries,
                        includeHidden,
                        entries,
                        ref truncated,
                        cancellationToken
                    );
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    public Task<CommandResult<ListDirectoryResult>> ListDirectoryAsync(
        string path,
        bool includeHidden,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Listing directory {Path} (includeHidden={IncludeHidden})", path, includeHidden);

        try
        {
            var dirInfo = new DirectoryInfo(path);
            if (!dirInfo.Exists)
            {
                return Task.FromResult(new CommandResult<ListDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.DirectoryNotFound, "The target directory was not found.")
                });
            }

            var directoriesList = new List<DirectoryEntry>();
            var filesList = new List<FileEntry>();

            foreach (var info in dirInfo.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 1. Skip reparse points
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                // 2. Skip hidden if not includeHidden
                if (!includeHidden && info.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    continue;
                }

                var isDir = info is DirectoryInfo;

                // 3. Centralized PathPolicy validation
                var error = _pathPolicy.Validate(info.FullName, out var normalizedPath, isDir);
                if (error is not null)
                {
                    continue;
                }

                if (isDir)
                {
                    directoriesList.Add(new DirectoryEntry
                    {
                        Name = info.Name,
                        Path = normalizedPath
                    });
                }
                else if (info is FileInfo fileInfo)
                {
                    filesList.Add(new FileEntry
                    {
                        Name = fileInfo.Name,
                        Path = normalizedPath,
                        Extension = fileInfo.Extension,
                        SizeBytes = fileInfo.Length,
                        LastWriteTimeUtc = fileInfo.LastWriteTimeUtc
                    });
                }
            }

            // Consistent sorting: alphabetical, directories first, then files
            var sortedDirs = directoriesList
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sortedFiles = filesList
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult(new CommandResult<ListDirectoryResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new ListDirectoryResult
                {
                    NormalizedPath = path,
                    Directories = sortedDirs,
                    Files = sortedFiles,
                    TotalDirectories = sortedDirs.Count,
                    TotalFiles = sortedFiles.Count
                }
            });
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(new CommandResult<ListDirectoryResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The directory listing operation was cancelled.")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list directory {Path}", path);
            return Task.FromResult(new CommandResult<ListDirectoryResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An error occurred while listing the directory.")
            });
        }
    }

    public Task<CommandResult<SearchFilesResult>> SearchFilesAsync(
        string path,
        string query,
        string mode,
        string? filePattern,
        bool caseSensitive,
        int maxResults,
        long maxFileBytes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Searching files in {Path} (query={Query}, mode={Mode}, pattern={Pattern})", path, query, mode, filePattern);

        if (string.IsNullOrWhiteSpace(mode) ||
            (!string.Equals(mode, "name", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(mode, "content", StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(new CommandResult<SearchFilesResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InvalidSearchMode, "Invalid search mode. Supported modes are 'name' or 'content'.")
            });
        }

        if (string.IsNullOrEmpty(query))
        {
            return Task.FromResult(new CommandResult<SearchFilesResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.SearchQueryRequired, "Search query is required and cannot be empty.")
            });
        }

        try
        {
            var dirInfo = new DirectoryInfo(path);
            if (!dirInfo.Exists)
            {
                return Task.FromResult(new CommandResult<SearchFilesResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.DirectoryNotFound, "The target directory was not found.")
                });
            }

            var matches = new List<SearchMatch>();
            var searchPattern = string.IsNullOrWhiteSpace(filePattern) ? "*" : filePattern;

            SearchRecursive(dirInfo, path, query, mode, searchPattern, caseSensitive, maxResults, maxFileBytes, matches, cancellationToken);

            return Task.FromResult(new CommandResult<SearchFilesResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new SearchFilesResult
                {
                    Matches = matches
                }
            });
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(new CommandResult<SearchFilesResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The search operation was cancelled.")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search files in {Path}", path);
            return Task.FromResult(new CommandResult<SearchFilesResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An error occurred during search.")
            });
        }
    }

    private void SearchRecursive(
        DirectoryInfo currentDir,
        string rootPath,
        string query,
        string mode,
        string searchPattern,
        bool caseSensitive,
        int maxResults,
        long maxFileBytes,
        List<SearchMatch> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (results.Count >= maxResults) return;

        // Skip reparse points
        if (currentDir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;

        // Centralized PathPolicy validation for directory
        var dirError = _pathPolicy.Validate(currentDir.FullName, out var normalizedDir, isDirectory: true);
        if (dirError is not null) return;

        try
        {
            foreach (var fileInfo in currentDir.EnumerateFiles(searchPattern))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (results.Count >= maxResults) return;

                // Skip reparse points
                if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                // Centralized PathPolicy validation for file
                var fileError = _pathPolicy.Validate(fileInfo.FullName, out var normalizedFile, isDirectory: false);
                if (fileError is not null) continue;

                // Check file size limit
                if (fileInfo.Length > maxFileBytes) continue;

                var relativePath = Path.GetRelativePath(rootPath, normalizedFile);

                if (string.Equals(mode, "name", StringComparison.OrdinalIgnoreCase))
                {
                    bool isMatch;
                    if (caseSensitive)
                    {
                        isMatch = fileInfo.Name.Contains(query) || relativePath.Contains(query);
                    }
                    else
                    {
                        isMatch = fileInfo.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || relativePath.Contains(query, StringComparison.OrdinalIgnoreCase);
                    }

                    if (isMatch)
                    {
                        results.Add(new SearchMatch
                        {
                            RelativePath = relativePath,
                            FullPath = normalizedFile,
                            MatchType = "name"
                        });
                    }
                }
                else if (string.Equals(mode, "content", StringComparison.OrdinalIgnoreCase))
                {
                    SearchInFileContent(normalizedFile, query, caseSensitive, maxFileBytes, maxResults, relativePath, results, cancellationToken);
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }

        try
        {
            foreach (var subDir in currentDir.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                SearchRecursive(subDir, rootPath, query, mode, searchPattern, caseSensitive, maxResults, maxFileBytes, results, cancellationToken);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    private void SearchInFileContent(
        string fullPath,
        string query,
        bool caseSensitive,
        long maxFileBytes,
        int maxResults,
        string relativePath,
        List<SearchMatch> results,
        CancellationToken cancellationToken)
    {
        // Double check size
        var info = new FileInfo(fullPath);
        if (info.Length > maxFileBytes) return;

        // Read first scan block to detect if it's binary
        try
        {
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int len = (int)Math.Min(fs.Length, 8000);
                var buffer = new byte[len];
                int bytesRead = fs.Read(buffer, 0, len);
                if (IsBinary(buffer))
                {
                    return; // Skip binary files
                }
            }
        }
        catch
        {
            return; // Skip read issues
        }

        // Scan line-by-line
        try
        {
            using (var reader = new StreamReader(fullPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                int lineNumber = 0;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (results.Count >= maxResults) return;

                    lineNumber++;
                    bool isMatch;
                    if (caseSensitive)
                    {
                        isMatch = line.Contains(query);
                    }
                    else
                    {
                        isMatch = line.Contains(query, StringComparison.OrdinalIgnoreCase);
                    }

                    if (isMatch)
                    {
                        var preview = line.Trim();
                        if (preview.Length > 200)
                        {
                            preview = preview.Substring(0, 197) + "...";
                        }

                        results.Add(new SearchMatch
                        {
                            RelativePath = relativePath,
                            FullPath = fullPath,
                            MatchType = "content",
                            LineNumber = lineNumber,
                            LinePreview = preview
                        });
                    }
                }
            }
        }
        catch
        {
            // Skip read failures
        }
    }

    private static string ComputeSha256(byte[] bytes)
    {
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static bool IsBinary(byte[] bytes)
    {
        int scanLength = Math.Min(bytes.Length, 8000);
        for (int i = 0; i < scanLength; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static (string Content, string EncodingName) DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            var content = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return (content, "utf-8-bom");
        }

        var text = Encoding.UTF8.GetString(bytes);
        return (text, "utf-8");
    }
}
