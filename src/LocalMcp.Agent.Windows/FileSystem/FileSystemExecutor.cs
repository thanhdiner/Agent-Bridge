using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalMcp.Contracts.Results;
using LocalMcp.Contracts.Commands;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LocalMcp.Agent.Windows.Security;

namespace LocalMcp.Agent.Windows.FileSystem;

public sealed class FileSystemExecutor : IFileSystemExecutor
{
    private static readonly Encoding StrictUtf8Encoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly IPathPolicy _pathPolicy;
    private readonly FileAccessOptions _options;
    private readonly ILogger<FileSystemExecutor> _logger;

    internal Func<string, Task>? OnBeforeContentReadHook { get; set; }
    internal Action<string>? OnDirectorySegmentCreatedHook { get; set; }
    internal Func<string, Task>? OnBeforeDirectoryDeleteHook { get; set; }

    public FileSystemExecutor(
        IPathPolicy pathPolicy,
        IOptions<FileAccessOptions> options,
        ILogger<FileSystemExecutor> logger)
    {
        _pathPolicy = pathPolicy;
        _options = options.Value;
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

            var sha256Hash = ComputeSha256(bytes);

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

            string content;
            string encoding;
            try
            {
                (content, encoding) = DecodeText(bytes);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "File {Path} contains invalid UTF-8 encoding.", path);
                return new CommandResult<ReadFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.UnsupportedTextEncoding, "Unsupported text encoding.")
                };
            }

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

    public async Task<CommandResult<ReadRangeResult>> ReadRangeAsync(
        string path,
        long startLine,
        int lineCount,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Reading line range from {Path} for command {CommandId} (startLine={StartLine}, lineCount={LineCount})",
            path,
            commandId,
            startLine,
            lineCount);

        if (startLine < 1)
        {
            return new CommandResult<ReadRangeResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InvalidRequest, "startLine must be greater than or equal to 1.")
            };
        }

        if (lineCount < 1 || lineCount > 1000)
        {
            return new CommandResult<ReadRangeResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InvalidRequest, "lineCount must be between 1 and 1000.")
            };
        }

        if (startLine > long.MaxValue - lineCount)
        {
            return new CommandResult<ReadRangeResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InvalidRequest, "The requested line range is too large.")
            };
        }

        var policyError = _pathPolicy.AuthorizeStat(path, out var physicalPath);
        if (policyError is not null)
        {
            return new CommandResult<ReadRangeResult>
            {
                CommandId = commandId,
                Success = false,
                Error = policyError
            };
        }

        if (Directory.Exists(physicalPath))
        {
            return new CommandResult<ReadRangeResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.AccessDenied, "The requested path is a directory, not a file.")
            };
        }

        if (!File.Exists(physicalPath))
        {
            return new CommandResult<ReadRangeResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.FileNotFound, "The requested file was not found.")
            };
        }

        try
        {
            using var stream = new FileStream(
                physicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            var prefix = new byte[4];
            var prefixLength = await ReadPrefixAsync(stream, prefix, cancellationToken);

            int bomLength;
            string encoding;
            if (prefixLength >= 4 &&
                ((prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0xFE && prefix[3] == 0xFF) ||
                 (prefix[0] == 0xFF && prefix[1] == 0xFE && prefix[2] == 0x00 && prefix[3] == 0x00)))
            {
                return new CommandResult<ReadRangeResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.UnsupportedTextEncoding, "Only UTF-8 text files are supported.")
                };
            }

            if (prefixLength >= 2 &&
                ((prefix[0] == 0xFF && prefix[1] == 0xFE) ||
                 (prefix[0] == 0xFE && prefix[1] == 0xFF)))
            {
                return new CommandResult<ReadRangeResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.UnsupportedTextEncoding, "Only UTF-8 text files are supported.")
                };
            }

            if (prefixLength >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
            {
                bomLength = 3;
                encoding = "utf-8-bom";
            }
            else
            {
                bomLength = 0;
                encoding = "utf-8";
            }

            stream.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                for (var i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] == 0)
                    {
                        return new CommandResult<ReadRangeResult>
                        {
                            CommandId = commandId,
                            Success = false,
                            Error = new CommandError(ErrorCodes.BinaryFileNotSupported, "Binary files are not supported.")
                        };
                    }
                }

                hash.AppendData(buffer, 0, bytesRead);
            }

            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

            stream.Position = bomLength;
            using var reader = new StreamReader(
                stream,
                StrictUtf8Encoding,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 81920,
                leaveOpen: true);

            var selectedLines = new List<string>(lineCount);
            long totalLines = 0;
            long selectedBytes = 0;
            var requestedEndExclusive = startLine + lineCount;

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                totalLines++;

                if (totalLines < startLine || totalLines >= requestedEndExclusive)
                {
                    continue;
                }

                var lineBytes = StrictUtf8Encoding.GetByteCount(line);
                if (selectedLines.Count > 0)
                {
                    selectedBytes++;
                }
                selectedBytes += lineBytes;

                if (selectedBytes > _options.MaxReadBytes)
                {
                    return new CommandResult<ReadRangeResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(
                            ErrorCodes.FileTooLarge,
                            $"The requested line range exceeds the allowed response limit of {_options.MaxReadBytes} bytes.")
                    };
                }

                selectedLines.Add(line);
            }

            var endLine = selectedLines.Count == 0
                ? startLine - 1
                : startLine + selectedLines.Count - 1;

            return new CommandResult<ReadRangeResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new ReadRangeResult
                {
                    Path = physicalPath,
                    StartLine = startLine,
                    EndLine = endLine,
                    TotalLines = totalLines,
                    Content = string.Join("\n", selectedLines),
                    Truncated = totalLines >= requestedEndExclusive,
                    Sha256 = sha256,
                    Encoding = encoding
                }
            };
        }
        catch (OperationCanceledException)
        {
            return new CommandResult<ReadRangeResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The line range read operation was cancelled.")
            };
        }
        catch (DecoderFallbackException ex)
        {
            _logger.LogWarning(ex, "File {Path} contains invalid UTF-8 while reading a line range.", physicalPath);
            return new CommandResult<ReadRangeResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.UnsupportedTextEncoding, "Only valid UTF-8 text files are supported.")
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied while reading a line range from {Path}.", physicalPath);
            return new CommandResult<ReadRangeResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.AccessDenied, "Access was denied while reading the file.")
            };
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "IO error while reading a line range from {Path}.", physicalPath);
            return new CommandResult<ReadRangeResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.ReadError, "An IO error occurred while reading the file.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read a line range from {Path} for command {CommandId}.", physicalPath, commandId);
            return new CommandResult<ReadRangeResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An unexpected error occurred while reading the file range.")
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

                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (!includeHidden && info.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    continue;
                }

                var isDir = info is DirectoryInfo;
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
        int maxEntries,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Listing directory {Path} (maxEntries={MaxEntries})", path, maxEntries);

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

                if (directoriesList.Count + filesList.Count >= maxEntries)
                {
                    break;
                }

                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (info.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    continue;
                }

                var isDir = info is DirectoryInfo;
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
        int maxResults,
        int maxDepth,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Searching files in {Path} (query={Query}, maxResults={MaxResults}, maxDepth={MaxDepth})", path, query, maxResults, maxDepth);

        if (string.IsNullOrEmpty(query))
        {
            return Task.FromResult(new CommandResult<SearchFilesResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.SearchQueryRequired, "Search query is required.")
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
            SearchRecursive(dirInfo, path, query, 1, maxDepth, maxResults, matches, cancellationToken);

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
        int currentDepth,
        int maxDepth,
        int maxResults,
        List<SearchMatch> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (results.Count >= maxResults) return;
        if (currentDepth > maxDepth) return;

        if (currentDir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;

        var dirError = _pathPolicy.Validate(currentDir.FullName, out var normalizedDir, isDirectory: true);
        if (dirError is not null) return;

        try
        {
            foreach (var fileInfo in currentDir.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (results.Count >= maxResults) return;

                if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                var fileError = _pathPolicy.Validate(fileInfo.FullName, out var normalizedFile, isDirectory: false);
                if (fileError is not null) continue;

                var relativePath = Path.GetRelativePath(rootPath, normalizedFile);

                if (fileInfo.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    relativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new SearchMatch
                    {
                        RelativePath = relativePath,
                        FullPath = normalizedFile,
                        MatchType = "name"
                    });
                }
                else
                {
                    SearchInFileContent(normalizedFile, query, 1048576, maxResults, relativePath, results, cancellationToken);
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
                SearchRecursive(subDir, rootPath, query, currentDepth + 1, maxDepth, maxResults, results, cancellationToken);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    private void SearchInFileContent(
        string fullPath,
        string query,
        long maxFileBytes,
        int maxResults,
        string relativePath,
        List<SearchMatch> results,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(fullPath);
        if (info.Length > maxFileBytes) return;

        try
        {
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int len = (int)Math.Min(fs.Length, 8000);
                var buffer = new byte[len];
                int bytesRead = fs.Read(buffer, 0, len);
                if (IsBinary(buffer))
                {
                    return;
                }
            }
        }
        catch
        {
            return;
        }

        try
        {
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(fs, StrictUtf8Encoding, detectEncodingFromByteOrderMarks: true))
            {
                int lineNumber = 0;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (results.Count >= maxResults) return;

                    lineNumber++;
                    if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
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
            // Skip invalid text file
        }
    }

    public async Task<CommandResult<WriteFileResult>> WriteFileAsync(
        string path,
        string content,
        string? expectedSha256,
        bool createIfMissing,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Writing file {Path} for command {CommandId}", path, commandId);

        string? previousSha256 = null;
        bool created;

        try
        {
            if (File.Exists(path))
            {
                byte[] currentBytes = await File.ReadAllBytesAsync(path, cancellationToken);
                if (IsBinary(currentBytes))
                {
                    return new CommandResult<WriteFileResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.BinaryFileNotSupported, "Binary files are not supported.")
                    };
                }

                var currentHash = ComputeSha256(currentBytes);
                if (string.IsNullOrEmpty(expectedSha256))
                {
                    return new CommandResult<WriteFileResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.ExpectedHashRequired, "expectedSha256 is required for modifying an existing file.")
                    };
                }
                if (!string.Equals(currentHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new CommandResult<WriteFileResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.FileConflict, "File conflict detected.")
                    };
                }

                previousSha256 = currentHash;
                created = false;
            }
            else
            {
                if (!createIfMissing)
                {
                    return new CommandResult<WriteFileResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.FileNotFound, "The target file does not exist.")
                    };
                }
                if (!string.IsNullOrEmpty(expectedSha256))
                {
                    return new CommandResult<WriteFileResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.InvalidRequest, "expectedSha256 must be null or empty when creating a new file.")
                    };
                }
                created = true;
            }

            var writeBytes = StrictUtf8Encoding.GetBytes(content);
            if (writeBytes.Length > _options.MaxWriteBytes)
            {
                return new CommandResult<WriteFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.FileTooLarge, "The content size exceeds the maximum allowed write limit.")
                };
            }

            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return new CommandResult<WriteFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.DirectoryNotFound, "The target directory was not found.")
                };
            }

            var tempPath = Path.Combine(dir, $".tmp_{Guid.NewGuid():N}");
            try
            {
                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await fs.WriteAsync(writeBytes.AsMemory(0, writeBytes.Length), cancellationToken);
                    fs.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (created)
                {
                    if (File.Exists(path))
                    {
                        return new CommandResult<WriteFileResult>
                        {
                            CommandId = commandId,
                            Success = false,
                            Error = new CommandError(ErrorCodes.FileAlreadyExists, "The file already exists.")
                        };
                    }
                    File.Move(tempPath, path);
                }
                else
                {
                    // Revalidate target state immediately before replacement
                    byte[] currentBytesCheck = await File.ReadAllBytesAsync(path, cancellationToken);
                    var currentHashCheck = ComputeSha256(currentBytesCheck);
                    if (!string.Equals(currentHashCheck, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return new CommandResult<WriteFileResult>
                        {
                            CommandId = commandId,
                            Success = false,
                            Error = new CommandError(ErrorCodes.FileConflict, "File conflict detected.")
                        };
                    }

                    File.Move(tempPath, path, overwrite: true);
                }
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "IOException during atomic write replacement.");
                if (created && File.Exists(path))
                {
                    return new CommandResult<WriteFileResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.FileAlreadyExists, "The file already exists.")
                    };
                }
                return new CommandResult<WriteFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.AtomicReplaceFailed, "Atomic write replacement failed.")
                };
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }

            var finalBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var finalHash = ComputeSha256(finalBytes);
            var fileInfo = new FileInfo(path);

            return new CommandResult<WriteFileResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new WriteFileResult
                {
                    Path = path,
                    Created = created,
                    BytesWritten = writeBytes.Length,
                    PreviousSha256 = previousSha256,
                    Sha256 = finalHash,
                    Encoding = "utf-8",
                    LastWriteTimeUtc = fileInfo.LastWriteTimeUtc
                }
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("File write was cancelled for command {CommandId}", commandId);
            return new CommandResult<WriteFileResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The file write operation was cancelled.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write file {Path} for command {CommandId}", path, commandId);
            return new CommandResult<WriteFileResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An error occurred while writing the file.")
            };
        }
    }

    public async Task<CommandResult<PatchFileResult>> PatchFileAsync(
        string path,
        string expectedSha256,
        List<PatchEdit> edits,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Patching file {Path} for command {CommandId}", path, commandId);

        if (edits == null || edits.Count == 0)
        {
            return new CommandResult<PatchFileResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.PatchEditsRequired, "At least one patch edit is required.")
            };
        }

        string tempPath = string.Empty;
        try
        {
            if (!File.Exists(path))
            {
                return new CommandResult<PatchFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.FileNotFound, "The target file does not exist.")
                };
            }

            var currentBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (IsBinary(currentBytes))
            {
                return new CommandResult<PatchFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.BinaryFileNotSupported, "Binary files are not supported.")
                };
            }

            string originalText;
            try
            {
                (originalText, _) = DecodeText(currentBytes);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Target file {Path} contains invalid UTF-8 encoding during patch.", path);
                return new CommandResult<PatchFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.UnsupportedTextEncoding, "Unsupported text encoding.")
                };
            }

            var currentHash = ComputeSha256(currentBytes);
            if (string.IsNullOrEmpty(expectedSha256))
            {
                return new CommandResult<PatchFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.ExpectedHashRequired, "expectedSha256 is required for modifying an existing file.")
                };
            }
            if (!string.Equals(currentHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new CommandResult<PatchFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.FileConflict, "File conflict detected.")
                };
            }

            var allSpans = new List<EditSpan>();
            foreach (var edit in edits)
            {
                if (string.IsNullOrEmpty(edit.OldText))
                {
                    return new CommandResult<PatchFileResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.InvalidRequest, "OldText in patch edit cannot be empty.")
                    };
                }

                var matches = new List<int>();
                int index = 0;
                while ((index = originalText.IndexOf(edit.OldText, index, StringComparison.Ordinal)) != -1)
                {
                    matches.Add(index);
                    index += edit.OldText.Length;
                }

                if (matches.Count == 0)
                {
                    return new CommandResult<PatchFileResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.PatchTargetNotFound, "Target text was not found in the file.")
                    };
                }

                if (!edit.ReplaceAll && matches.Count > 1)
                {
                    return new CommandResult<PatchFileResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.PatchTargetAmbiguous, "Target text has multiple occurrences in the file.")
                    };
                }

                foreach (var match in matches)
                {
                    allSpans.Add(new EditSpan
                    {
                        Start = match,
                        End = match + edit.OldText.Length,
                        NewText = edit.NewText
                    });
                }
            }

            for (int i = 0; i < allSpans.Count; i++)
            {
                for (int j = i + 1; j < allSpans.Count; j++)
                {
                    var s1 = allSpans[i];
                    var s2 = allSpans[j];
                    if (s1.Start < s2.End && s2.Start < s1.End)
                    {
                        return new CommandResult<PatchFileResult>
                        {
                            CommandId = commandId,
                            Success = false,
                            Error = new CommandError(ErrorCodes.PatchEditsOverlap, "The requested patch edits contain overlapping target spans.")
                        };
                    }
                }
            }

            var sb = new StringBuilder(originalText);
            foreach (var span in allSpans.OrderByDescending(s => s.Start))
            {
                sb.Remove(span.Start, span.End - span.Start);
                sb.Insert(span.Start, span.NewText);
            }
            var updatedText = sb.ToString();

            var writeBytes = StrictUtf8Encoding.GetBytes(updatedText);
            if (writeBytes.Length > _options.MaxWriteBytes)
            {
                return new CommandResult<PatchFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.FileTooLarge, "The patched content size exceeds the maximum allowed write limit.")
                };
            }

            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return new CommandResult<PatchFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.DirectoryNotFound, "The target directory was not found.")
                };
            }

            tempPath = Path.Combine(dir, $".tmp_{Guid.NewGuid():N}");

            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await fs.WriteAsync(writeBytes.AsMemory(0, writeBytes.Length), cancellationToken);
                fs.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Revalidate target state immediately before replacement
            byte[] currentBytesCheck = await File.ReadAllBytesAsync(path, cancellationToken);
            var currentHashCheck = ComputeSha256(currentBytesCheck);
            if (!string.Equals(currentHashCheck, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new CommandResult<PatchFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.FileConflict, "File conflict detected.")
                };
            }

            File.Move(tempPath, path, overwrite: true);

            var finalBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var finalHash = ComputeSha256(finalBytes);
            var fileInfo = new FileInfo(path);

            return new CommandResult<PatchFileResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new PatchFileResult
                {
                    Path = path,
                    EditsApplied = edits.Count,
                    ReplacementsMade = allSpans.Count,
                    BytesWritten = writeBytes.Length,
                    PreviousSha256 = currentHash,
                    Sha256 = finalHash,
                    LastWriteTimeUtc = fileInfo.LastWriteTimeUtc
                }
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("File patch was cancelled for command {CommandId}", commandId);
            return new CommandResult<PatchFileResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The file patch operation was cancelled.")
            };
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IOException during atomic patch replacement.");
            return new CommandResult<PatchFileResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.AtomicReplaceFailed, "Atomic patch replacement failed.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to patch file {Path} for command {CommandId}", path, commandId);
            return new CommandResult<PatchFileResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An error occurred while patching the file.")
            };
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public Task<CommandResult<CreateDirectoryResult>> CreateDirectoryAsync(
        string path,
        bool recursive,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating directory {Path} for command {CommandId} (recursive={Recursive})", path, commandId, recursive);

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(new CommandResult<CreateDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.")
                });
            }

            var policyError = _pathPolicy.AuthorizeCreateDirectory(path, out var physicalPath, recursive);
            if (policyError is not null)
            {
                return Task.FromResult(new CommandResult<CreateDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = policyError
                });
            }

            var current = physicalPath;
            var pathStack = new Stack<string>();
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current) || File.Exists(current))
                {
                    var attrs = File.GetAttributes(current);
                    if (attrs.HasFlag(FileAttributes.ReparsePoint))
                    {
                        return Task.FromResult(new CommandResult<CreateDirectoryResult>
                        {
                            CommandId = commandId,
                            Success = false,
                            Error = new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the path.")
                        });
                    }
                }

                if (Directory.Exists(current))
                {
                    break;
                }

                pathStack.Push(current);
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current)
                {
                    break;
                }
                current = parent;
            }

            var ancestor = current;
            if (string.IsNullOrEmpty(ancestor) || !Directory.Exists(ancestor))
            {
                return Task.FromResult(new CommandResult<CreateDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.DirectoryNotFound, "Parent directory does not exist.")
                });
            }

            if (!recursive && pathStack.Count > 1)
            {
                return Task.FromResult(new CommandResult<CreateDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.DirectoryNotFound, "The parent directory was not found.")
                });
            }

            var createdDirectories = new List<string>();

            while (pathStack.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    RollbackCreatedDirectories(createdDirectories);
                    return Task.FromResult(new CommandResult<CreateDirectoryResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.")
                    });
                }

                var nextDir = pathStack.Pop();
                var nextDirName = Path.GetFileName(nextDir);
                var segmentError = ValidateDirectoryName(nextDirName);
                if (segmentError is not null)
                {
                    RollbackCreatedDirectories(createdDirectories);
                    return Task.FromResult(new CommandResult<CreateDirectoryResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = segmentError
                    });
                }

                if (Directory.Exists(nextDir) || File.Exists(nextDir))
                {
                    var originalAttrs = File.GetAttributes(nextDir);
                    if (originalAttrs.HasFlag(FileAttributes.ReparsePoint))
                    {
                        RollbackCreatedDirectories(createdDirectories);
                        return Task.FromResult(new CommandResult<CreateDirectoryResult>
                        {
                            CommandId = commandId,
                            Success = false,
                            Error = new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed.")
                        });
                    }
                }

                try
                {
                    Directory.CreateDirectory(nextDir);
                    createdDirectories.Add(nextDir);
                }
                catch (Exception ex)
                {
                    RollbackCreatedDirectories(createdDirectories);
                    return Task.FromResult(new CommandResult<CreateDirectoryResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.AccessDenied, $"Failed to create directory '{nextDirName}': {ex.GetType().Name}")
                    });
                }

                if (OnDirectorySegmentCreatedHook is not null)
                {
                    OnDirectorySegmentCreatedHook(nextDir);
                }

                var verifyError = VerifyDirectoryAfterCreation(nextDir);
                if (verifyError is not null)
                {
                    RollbackCreatedDirectories(createdDirectories);
                    return Task.FromResult(new CommandResult<CreateDirectoryResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = verifyError
                    });
                }
            }

            return Task.FromResult(new CommandResult<CreateDirectoryResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new CreateDirectoryResult
                {
                    Path = physicalPath,
                    Created = createdDirectories.Count > 0,
                    DirectoriesCreated = createdDirectories
                }
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Unauthorized access creating directory {Path} for command {CommandId}", path, commandId);
            return Task.FromResult(new CommandResult<CreateDirectoryResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.AccessDenied, "Access denied to create the directory.")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create directory {Path} for command {CommandId}", path, commandId);
            return Task.FromResult(new CommandResult<CreateDirectoryResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An error occurred while creating the directory.")
            });
        }
    }

    public async Task<CommandResult<StatResult>> StatAsync(
        string path,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting stats for {Path} for command {CommandId}", path, commandId);

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new CommandResult<StatResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.")
                };
            }

            var policyError = _pathPolicy.AuthorizeStat(path, out var physicalPath);
            if (policyError is not null)
            {
                return new CommandResult<StatResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = policyError
                };
            }

            var result = new StatResult();

            if (File.Exists(physicalPath))
            {
                var fileInfo = new FileInfo(physicalPath);
                result.Exists = true;
                result.Type = "file";
                result.Size = fileInfo.Length;
                result.ReadOnly = fileInfo.IsReadOnly;
                result.LastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
                result.IsReparsePoint = fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);

                if (OnBeforeContentReadHook is not null)
                {
                    await OnBeforeContentReadHook(physicalPath);
                }

                try
                {
                    using (var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
                    {
                        var streamLength = stream.Length;
                        if (streamLength <= _options.MaxReadBytes)
                        {
                            var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
                            result.Sha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();

                            stream.Position = 0;

                            int maxBuffer = (int)streamLength + 1;
                            byte[] buffer = new byte[maxBuffer];
                            int totalRead = 0;
                            int read;
                            while ((read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, cancellationToken)) > 0)
                            {
                                totalRead += read;
                                if (totalRead > _options.MaxReadBytes)
                                {
                                    break;
                                }
                            }

                            if (totalRead > _options.MaxReadBytes)
                            {
                                result.ContentMetadataSkipped = true;
                                result.Sha256 = null;
                                result.Encoding = null;
                            }
                            else
                            {
                                byte[] contentBytes = new byte[totalRead];
                                Array.Copy(buffer, contentBytes, totalRead);

                                bool isBinary = IsBinary(contentBytes);
                                string? detectedEncoding = null;
                                if (!isBinary)
                                {
                                    try
                                    {
                                        var (_, encodingName) = DecodeText(contentBytes);
                                        detectedEncoding = encodingName;
                                    }
                                    catch (Exception ex) when (ex is DecoderFallbackException || ex is ArgumentException)
                                    {
                                        // Invalid UTF-8
                                    }
                                }

                                result.Encoding = detectedEncoding;
                                result.ContentMetadataAvailable = true;
                            }
                        }
                        else
                        {
                            result.ContentMetadataSkipped = true;
                        }
                    }
                }
                catch (IOException)
                {
                    result.ContentMetadataAvailable = false;
                    result.ContentMetadataErrorCode = "IO_ERROR";
                }
                catch (UnauthorizedAccessException)
                {
                    result.ContentMetadataAvailable = false;
                    result.ContentMetadataErrorCode = "ACCESS_DENIED";
                }
                catch (Exception)
                {
                    result.ContentMetadataAvailable = false;
                    result.ContentMetadataErrorCode = "READ_ERROR";
                }
            }
            else if (Directory.Exists(physicalPath))
            {
                var dirInfo = new DirectoryInfo(physicalPath);
                result.Exists = true;
                result.Type = "directory";
                result.ReadOnly = false;
                result.LastWriteTimeUtc = dirInfo.LastWriteTimeUtc;
                result.IsReparsePoint = dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            else
            {
                result.Exists = false;
            }

            return new CommandResult<StatResult>
            {
                CommandId = commandId,
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get stats for path {Path} for command {CommandId}", path, commandId);
            return new CommandResult<StatResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An error occurred while retrieving file status.")
            };
        }
    }

    private void RollbackCreatedDirectories(List<string> createdDirectories)
    {
        for (int i = createdDirectories.Count - 1; i >= 0; i--)
        {
            var dir = createdDirectories[i];
            try
            {
                if (Directory.Exists(dir) || File.Exists(dir))
                {
                    var attrs = File.GetAttributes(dir);
                    if (attrs.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    var physicalPath = ResolvePhysicalPath(dir);
                    if (!string.Equals(Path.GetFullPath(dir), physicalPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (Directory.Exists(dir))
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            continue;
                        }

                        if (Directory.GetFileSystemEntries(dir).Length == 0)
                        {
                            Directory.Delete(dir);
                        }
                    }
                }
            }
            catch
            {
                // Best effort
            }
        }
    }

    private CommandError? ValidateDirectoryName(string dirName)
    {
        if (string.IsNullOrEmpty(dirName))
        {
            return null;
        }

        if (_options.DeniedSegments.Any(ds => string.Equals(ds, dirName, StringComparison.OrdinalIgnoreCase)))
        {
            return new CommandError(ErrorCodes.AccessDenied, $"Access denied to directory containing segment '{dirName}'.");
        }

        if (_options.DeniedFileNames.Any(df => PathPolicy.MatchFileName(dirName, df)) ||
            _options.DeniedWriteFileNames.Any(dw => PathPolicy.MatchFileName(dirName, dw)))
        {
            return new CommandError(ErrorCodes.AccessDenied, $"Access denied to directory '{dirName}'.");
        }

        return null;
    }

    private CommandError? VerifyDirectoryAfterCreation(string dirPath)
    {
        try
        {
            if (Directory.Exists(dirPath) || File.Exists(dirPath))
            {
                var originalAttrs = File.GetAttributes(dirPath);
                if (originalAttrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed.");
                }
            }

            var physicalPath = ResolvePhysicalPath(dirPath);

            var dirInfo = new DirectoryInfo(physicalPath);
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed.");
            }

            bool inWritableRoot = false;
            foreach (var root in _options.WritableRoots)
            {
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var fullPath = physicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    inWritableRoot = true;
                    break;
                }
            }
            if (!inWritableRoot)
            {
                return new CommandError(ErrorCodes.WriteNotAllowed, "The directory escaped the writable root directory.");
            }

            bool inAllowedRoot = false;
            foreach (var root in _options.AllowedRoots)
            {
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var fullPath = physicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    inAllowedRoot = true;
                    break;
                }
            }
            if (!inAllowedRoot)
            {
                return new CommandError(ErrorCodes.PathOutsideAllowedRoot, "The directory escaped the allowed root directory.");
            }
        }
        catch (Exception)
        {
            return new CommandError(ErrorCodes.AccessDenied, "Verification of created directory failed.");
        }

        return null;
    }

    private string ResolvePhysicalPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            var fsi = new DirectoryInfo(current);
            if (fsi.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var target = fsi.ResolveLinkTarget(true);
                if (target is not null)
                {
                    current = target.FullName;
                    continue;
                }
            }

            var currentParent = Path.GetDirectoryName(current);
            if (currentParent is null)
            {
                break;
            }

            var resolvedCurrentParent = ResolvePhysicalPath(currentParent);
            if (!string.Equals(resolvedCurrentParent, currentParent, StringComparison.OrdinalIgnoreCase))
            {
                current = Path.Combine(resolvedCurrentParent, Path.GetFileName(current));
                continue;
            }

            break;
        }

        return Path.GetFullPath(current);
    }



    private static async Task<int> ReadPrefixAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer,
                totalRead,
                buffer.Length - totalRead,
                cancellationToken);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }
        return totalRead;
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
            var content = StrictUtf8Encoding.GetString(bytes, 3, bytes.Length - 3);
            return (content, "utf-8-bom");
        }

        var text = StrictUtf8Encoding.GetString(bytes);
        return (text, "utf-8");
    }

    private sealed class EditSpan
    {
        public int Start { get; set; }
        public int End { get; set; }
        public string NewText { get; set; } = string.Empty;
    }

    // ── fs_move ──────────────────────────────────────────────────────────────

    public async Task<CommandResult<MoveResult>> MoveAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        string? expectedSha256,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new CommandResult<MoveResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.")
            };

        var policyError = _pathPolicy.AuthorizeMove(sourcePath, destinationPath, overwrite, out var physicalSource, out var physicalDest);
        if (policyError is not null)
            return new CommandResult<MoveResult> { CommandId = commandId, Success = false, Error = policyError };

        bool isDirectory = Directory.Exists(physicalSource);

        // SHA-256 concurrency check on files only
        if (!isDirectory && !string.IsNullOrEmpty(expectedSha256))
        {
            try
            {
                using var stream = new FileStream(physicalSource, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
                var actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                if (!string.Equals(actualHash, expectedSha256.ToLowerInvariant(), StringComparison.Ordinal))
                    return new CommandResult<MoveResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.ConcurrencyConflict, "Source file has changed since the expected SHA-256 was computed.")
                    };
            }
            catch (OperationCanceledException)
            {
                return new CommandResult<MoveResult> { CommandId = commandId, Success = false, Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.") };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute SHA-256 for pre-move verification. CommandId: {CommandId}", commandId);
                return new CommandResult<MoveResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.ReadError, "Failed to read source file for SHA-256 verification.")
                };
            }
        }

        try
        {
            if (isDirectory)
            {
                Directory.Move(physicalSource, physicalDest);
            }
            else
            {
                File.Move(physicalSource, physicalDest, overwrite);
            }
        }
        catch (OperationCanceledException)
        {
            return new CommandResult<MoveResult> { CommandId = commandId, Success = false, Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.") };
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "IO error during move. CommandId: {CommandId}", commandId);
            return new CommandResult<MoveResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.WriteError, "Failed to move the path due to an IO error.")
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied during move. CommandId: {CommandId}", commandId);
            return new CommandResult<MoveResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.AccessDenied, "Access was denied when attempting to move the path.")
            };
        }

        DateTime lastWrite;
        try
        {
            lastWrite = isDirectory
                ? new DirectoryInfo(physicalDest).LastWriteTimeUtc
                : new FileInfo(physicalDest).LastWriteTimeUtc;
        }
        catch
        {
            lastWrite = DateTime.UtcNow;
        }

        return new CommandResult<MoveResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new MoveResult
            {
                Path = physicalDest,
                IsDirectory = isDirectory,
                LastWriteTimeUtc = lastWrite
            }
        };
    }

    // ── fs_copy ──────────────────────────────────────────────────────────────

    public Task<CommandResult<CopyResult>> CopyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        string? expectedSourceSha256,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        return CopyAsync(sourcePath, destinationPath, overwrite, expectedSourceSha256, long.MaxValue, commandId, cancellationToken);
    }

    public async Task<CommandResult<CopyResult>> CopyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        string? expectedSourceSha256,
        long maxTotalBytes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new CommandResult<CopyResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.")
            };

        var policyError = _pathPolicy.AuthorizeCopy(sourcePath, destinationPath, overwrite, out var physicalSource, out var physicalDest);
        if (policyError is not null)
            return new CommandResult<CopyResult> { CommandId = commandId, Success = false, Error = policyError };

        var destDir = Path.GetDirectoryName(physicalDest)!;
        var tempPath = Path.Combine(destDir, $"copy-temp-{Guid.NewGuid():N}.tmp");

        string? preCopyHash = null;
        long bytesCopied = 0;
        string? copiedHash = null;

        try
        {
            // Step 1: Open source and compute pre-copy hash + stream copy to temp
            using (var srcStream = new FileStream(physicalSource, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                // Compute source hash before copying
                preCopyHash = Convert.ToHexString(await SHA256.HashDataAsync(srcStream, cancellationToken)).ToLowerInvariant();

                // Validate expectedSourceSha256 if provided
                if (!string.IsNullOrEmpty(expectedSourceSha256) &&
                    !string.Equals(preCopyHash, expectedSourceSha256.ToLowerInvariant(), StringComparison.Ordinal))
                    return new CommandResult<CopyResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.ConcurrencyConflict, "Source file has changed since the expected SHA-256 was computed.")
                    };

                srcStream.Position = 0;

                // Step 2: Copy to temp file while computing dest hash
                using var destStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await srcStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    sha.AppendData(buffer, 0, read);
                    bytesCopied += read;
                    if (bytesCopied > maxTotalBytes)
                        return new CommandResult<CopyResult>
                        {
                            CommandId = commandId,
                            Success = false,
                            Error = new CommandError(ErrorCodes.FileTooLarge, "The source file exceeded maxTotalBytes during copy.")
                        };
                }

                await destStream.FlushAsync(cancellationToken);
                destStream.SafeFileHandle.GetHashCode(); // ensure handle alive for Flush
                var rawHash = sha.GetHashAndReset();
                copiedHash = Convert.ToHexString(rawHash).ToLowerInvariant();
            }

            // Step 3: Revalidate source SHA-256 before final swap
            using (var revalidStream = new FileStream(physicalSource, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                var revalidHash = Convert.ToHexString(await SHA256.HashDataAsync(revalidStream, cancellationToken)).ToLowerInvariant();
                if (!string.Equals(revalidHash, preCopyHash, StringComparison.Ordinal))
                    return new CommandResult<CopyResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.ConcurrencyConflict, "Source file changed during the copy operation.")
                    };
            }

            // Step 4: Atomic swap – move temp to final destination
            if (overwrite && File.Exists(physicalDest))
            {
                File.Move(tempPath, physicalDest, overwrite: true);
            }
            else
            {
                // If destination appeared concurrently and overwrite is false, fail cleanly
                if (File.Exists(physicalDest))
                    return new CommandResult<CopyResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.AccessDenied, "The destination file appeared concurrently.")
                    };
                File.Move(tempPath, physicalDest, overwrite: false);
            }
            tempPath = null; // ownership transferred – no cleanup needed
        }
        catch (OperationCanceledException)
        {
            return new CommandResult<CopyResult> { CommandId = commandId, Success = false, Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.") };
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "IO error during copy. CommandId: {CommandId}", commandId);
            return new CommandResult<CopyResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.WriteError, "Failed to copy the file due to an IO error.")
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied during copy. CommandId: {CommandId}", commandId);
            return new CommandResult<CopyResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.AccessDenied, "Access was denied when attempting to copy the file.")
            };
        }
        finally
        {
            // Clean up temp file if ownership was not transferred
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); }
                catch { /* best-effort */ }
            }
        }

        var destInfo = new FileInfo(physicalDest);
        return new CommandResult<CopyResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new CopyResult
            {
                Path = physicalDest,
                IsDirectory = false,
                FilesCopied = 1,
                DirectoriesCreated = 0,
                BytesCopied = bytesCopied,
                Sha256 = copiedHash!,
                LastWriteTimeUtc = destInfo.LastWriteTimeUtc
            }
        };
    }

    // ── fs_delete ────────────────────────────────────────────────────────────

    public async Task<CommandResult<DeleteResult>> DeleteAsync(
        string path,
        string? expectedSha256,
        bool missingOk,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new CommandResult<DeleteResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.")
            };
        }

        var policyError = _pathPolicy.AuthorizeDeleteFile(path, missingOk, out var physicalPath);
        if (policyError is not null)
            return new CommandResult<DeleteResult> { CommandId = commandId, Success = false, Error = policyError };

        if (Directory.Exists(physicalPath))
        {
            return new CommandResult<DeleteResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.AccessDenied, "Deleting directories is not supported.")
            };
        }

        if (!File.Exists(physicalPath))
        {
            if (!missingOk)
            {
                return new CommandResult<DeleteResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.FileNotFound, "The requested file was not found.")
                };
            }

            return new CommandResult<DeleteResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new DeleteResult
                {
                    Path = physicalPath,
                    BytesDeleted = 0,
                    Sha256 = null
                }
            };
        }

        try
        {
            var attributes = File.GetAttributes(physicalPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new CommandResult<DeleteResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the delete path.")
                };
            }

            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                return new CommandResult<DeleteResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.FileReadOnly, "The target file is read-only.")
                };
            }

            long bytesDeleted;
            string initialHash;
            using (var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                bytesDeleted = stream.Length;
                initialHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256) &&
                !string.Equals(initialHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new CommandResult<DeleteResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.ConcurrencyConflict, "Target file has changed since the expected SHA-256 was computed.")
                };
            }

            cancellationToken.ThrowIfCancellationRequested();

            var revalidationError = _pathPolicy.AuthorizeDeleteFile(physicalPath, missingOk: false, out var revalidatedPath);
            if (revalidationError is not null)
                return new CommandResult<DeleteResult> { CommandId = commandId, Success = false, Error = revalidationError };

            if (!string.Equals(physicalPath, revalidatedPath, StringComparison.OrdinalIgnoreCase))
            {
                return new CommandResult<DeleteResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.ConcurrencyConflict, "Target path changed during delete validation.")
                };
            }

            string finalHash;
            long finalLength;
            using (var stream = new FileStream(revalidatedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                finalLength = stream.Length;
                finalHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            }

            if (finalLength != bytesDeleted || !string.Equals(finalHash, initialHash, StringComparison.Ordinal))
            {
                return new CommandResult<DeleteResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.ConcurrencyConflict, "Target file changed during delete validation.")
                };
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(revalidatedPath);

            if (File.Exists(revalidatedPath))
            {
                return new CommandResult<DeleteResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.WriteError, "The file still exists after the delete operation.")
                };
            }

            return new CommandResult<DeleteResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new DeleteResult
                {
                    Path = revalidatedPath,
                    BytesDeleted = bytesDeleted,
                    Sha256 = initialHash
                }
            };
        }
        catch (OperationCanceledException)
        {
            return new CommandResult<DeleteResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.")
            };
        }
        catch (FileNotFoundException)
        {
            if (missingOk)
            {
                return new CommandResult<DeleteResult>
                {
                    CommandId = commandId,
                    Success = true,
                    Data = new DeleteResult { Path = physicalPath, BytesDeleted = 0, Sha256 = null }
                };
            }

            return new CommandResult<DeleteResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.FileNotFound, "The requested file was not found.")
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied during delete. CommandId: {CommandId}", commandId);
            return new CommandResult<DeleteResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.AccessDenied, "Access was denied when attempting to delete the file.")
            };
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "IO error during delete. CommandId: {CommandId}", commandId);
            return new CommandResult<DeleteResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.WriteError, "Failed to delete the file due to an IO error.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during delete. CommandId: {CommandId}", commandId);
            return new CommandResult<DeleteResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An unexpected error occurred while deleting the file.")
            };
        }
    }

    // ── fs_rmdir ─────────────────────────────────────────────────────────────

    public async Task<CommandResult<RemoveDirectoryResult>> RemoveDirectoryAsync(
        string path,
        bool missingOk,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new CommandResult<RemoveDirectoryResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.")
            };
        }

        var policyError = _pathPolicy.AuthorizeRemoveDirectory(path, missingOk, out var physicalPath);
        if (policyError is not null)
            return new CommandResult<RemoveDirectoryResult> { CommandId = commandId, Success = false, Error = policyError };

        if (!Directory.Exists(physicalPath))
        {
            if (File.Exists(physicalPath))
            {
                return new CommandResult<RemoveDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.ConcurrencyConflict, "Target path changed from a directory to a file.")
                };
            }

            if (!missingOk)
            {
                return new CommandResult<RemoveDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.DirectoryNotFound, "The requested directory was not found.")
                };
            }

            return new CommandResult<RemoveDirectoryResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new RemoveDirectoryResult
                {
                    Path = physicalPath,
                    Removed = false
                }
            };
        }

        try
        {
            var attributes = File.GetAttributes(physicalPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new CommandResult<RemoveDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the directory removal path.")
                };
            }

            if (Directory.EnumerateFileSystemEntries(physicalPath).Any())
            {
                return new CommandResult<RemoveDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.DirectoryNotEmpty, "The directory is not empty.")
                };
            }

            if (OnBeforeDirectoryDeleteHook is not null)
                await OnBeforeDirectoryDeleteHook(physicalPath);

            cancellationToken.ThrowIfCancellationRequested();

            var revalidationError = _pathPolicy.AuthorizeRemoveDirectory(physicalPath, missingOk, out var revalidatedPath);
            if (revalidationError is not null)
                return new CommandResult<RemoveDirectoryResult> { CommandId = commandId, Success = false, Error = revalidationError };

            if (!string.Equals(physicalPath, revalidatedPath, StringComparison.OrdinalIgnoreCase))
            {
                return new CommandResult<RemoveDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.ConcurrencyConflict, "Target directory path changed during removal validation.")
                };
            }

            if (!Directory.Exists(revalidatedPath))
            {
                if (missingOk)
                {
                    return new CommandResult<RemoveDirectoryResult>
                    {
                        CommandId = commandId,
                        Success = true,
                        Data = new RemoveDirectoryResult { Path = revalidatedPath, Removed = false }
                    };
                }

                return new CommandResult<RemoveDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.DirectoryNotFound, "The requested directory was not found.")
                };
            }

            attributes = File.GetAttributes(revalidatedPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new CommandResult<RemoveDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the directory removal path.")
                };
            }

            if (Directory.EnumerateFileSystemEntries(revalidatedPath).Any())
            {
                return new CommandResult<RemoveDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.DirectoryNotEmpty, "The directory is not empty.")
                };
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(revalidatedPath, recursive: false);

            if (Directory.Exists(revalidatedPath))
            {
                return new CommandResult<RemoveDirectoryResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.WriteError, "The directory still exists after the removal operation.")
                };
            }

            return new CommandResult<RemoveDirectoryResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new RemoveDirectoryResult
                {
                    Path = revalidatedPath,
                    Removed = true
                }
            };
        }
        catch (OperationCanceledException)
        {
            return new CommandResult<RemoveDirectoryResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.CommandCancelled, "The command was cancelled.")
            };
        }
        catch (DirectoryNotFoundException)
        {
            if (missingOk)
            {
                return new CommandResult<RemoveDirectoryResult>
                {
                    CommandId = commandId,
                    Success = true,
                    Data = new RemoveDirectoryResult { Path = physicalPath, Removed = false }
                };
            }

            return new CommandResult<RemoveDirectoryResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.DirectoryNotFound, "The requested directory was not found.")
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied during directory removal. CommandId: {CommandId}", commandId);
            return new CommandResult<RemoveDirectoryResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.AccessDenied, "Access was denied when attempting to remove the directory.")
            };
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "IO error during directory removal. CommandId: {CommandId}", commandId);

            try
            {
                if (Directory.Exists(physicalPath) && Directory.EnumerateFileSystemEntries(physicalPath).Any())
                {
                    return new CommandResult<RemoveDirectoryResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.DirectoryNotEmpty, "The directory is not empty.")
                    };
                }
            }
            catch
            {
                // Preserve the original IO error mapping.
            }

            return new CommandResult<RemoveDirectoryResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.WriteError, "Failed to remove the directory due to an IO error.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during directory removal. CommandId: {CommandId}", commandId);
            return new CommandResult<RemoveDirectoryResult>
            {
                CommandId = commandId,
                Success = false,
                Error = new CommandError(ErrorCodes.InternalError, "An unexpected error occurred while removing the directory.")
            };
        }
    }
}
