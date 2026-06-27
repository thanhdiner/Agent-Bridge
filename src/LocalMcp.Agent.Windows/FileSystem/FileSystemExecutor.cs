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
}
