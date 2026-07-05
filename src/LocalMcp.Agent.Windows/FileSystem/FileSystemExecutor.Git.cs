using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.FileSystem;

public sealed partial class FileSystemExecutor
{
    private static readonly Encoding LenientUtf8Encoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    public async Task<CommandResult<GitStatusResult>> GitStatusAsync(
        string path,
        bool includeUntracked,
        int maxEntries,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (maxEntries < 1 || maxEntries > 5000)
            return GitStatusFailure(commandId, ErrorCodes.InvalidRequest, "maxEntries must be between 1 and 5000.");

        try
        {
            var resolution = await ResolveGitRepositoryRootAsync(path, commandId, cancellationToken);
            if (resolution.Error is not null)
                return GitStatusFailure(commandId, resolution.Error.Code, resolution.Error.Message);

            var repositoryRoot = resolution.Root!;
            var configurationError = await ValidateSafeGitConfigurationAsync(
                repositoryRoot,
                cancellationToken);
            if (configurationError is not null)
                return GitStatusFailure(commandId, configurationError.Code, configurationError.Message);

            var statusArguments = new List<string>
            {
                "status",
                "--porcelain=v1",
                "-z",
                includeUntracked ? "--untracked-files=all" : "--untracked-files=no",
                "--ignore-submodules=none"
            };

            var statusProcess = await RunGitAsync(
                repositoryRoot,
                statusArguments,
                maxStdoutBytes: 8_388_608,
                timeout: TimeSpan.FromSeconds(20),
                cancellationToken);

            if (statusProcess.TimedOut)
                return GitStatusFailure(commandId, ErrorCodes.CommandTimeout, "Git status timed out.");
            if (statusProcess.StartError is not null)
                return GitStatusFailure(commandId, ErrorCodes.GitNotAvailable, "Git is not available on the Windows agent.");
            if (statusProcess.ExitCode != 0)
                return GitStatusFailure(commandId, ErrorCodes.GitCommandFailed, "Git could not read the repository status.");
            if (statusProcess.StdoutTruncated)
                return GitStatusFailure(commandId, ErrorCodes.FileTooLarge, "Git status output exceeded the allowed response limit.");

            var allEntries = ParseGitStatusPorcelain(statusProcess.Stdout);
            var visibleEntries = allEntries
                .Where(entry => IsGitStatusEntryAuthorized(repositoryRoot, entry))
                .ToList();
            var omittedEntries = allEntries.Count - visibleEntries.Count;
            var entriesTruncated = visibleEntries.Count > maxEntries;
            var entries = entriesTruncated
                ? visibleEntries.Take(maxEntries).ToList()
                : visibleEntries;

            var branch = await GetOptionalGitOutputAsync(
                repositoryRoot,
                ["symbolic-ref", "--quiet", "--short", "HEAD"],
                cancellationToken);
            var headCommit = await GetOptionalGitOutputAsync(
                repositoryRoot,
                ["rev-parse", "--verify", "--short=12", "HEAD"],
                cancellationToken);
            var upstream = await GetOptionalGitOutputAsync(
                repositoryRoot,
                ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"],
                cancellationToken);

            var ahead = 0;
            var behind = 0;
            if (!string.IsNullOrWhiteSpace(upstream) && !string.IsNullOrWhiteSpace(headCommit))
            {
                var counts = await GetOptionalGitOutputAsync(
                    repositoryRoot,
                    ["rev-list", "--left-right", "--count", "HEAD...@{upstream}"],
                    cancellationToken);
                ParseAheadBehind(counts, out ahead, out behind);
            }

            return new CommandResult<GitStatusResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new GitStatusResult
                {
                    RepositoryRoot = repositoryRoot,
                    Branch = branch,
                    DetachedHead = branch is null && headCommit is not null,
                    HeadCommit = headCommit,
                    Upstream = upstream,
                    Ahead = ahead,
                    Behind = behind,
                    IncludeUntracked = includeUntracked,
                    IsClean = allEntries.Count == 0,
                    Entries = entries,
                    OmittedEntries = omittedEntries,
                    Truncated = entriesTruncated
                }
            };
        }
        catch (OperationCanceledException)
        {
            return GitStatusFailure(commandId, ErrorCodes.CommandCancelled, "The Git status command was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Git status failure for command {CommandId}", commandId);
            return GitStatusFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while reading Git status.");
        }
    }

    public async Task<CommandResult<GitDiffResult>> GitDiffAsync(
        string path,
        bool staged,
        bool includeUntracked,
        IReadOnlyList<string> pathSpecs,
        int contextLines,
        int maxBytes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (contextLines < 0 || contextLines > 20)
            return GitDiffFailure(commandId, ErrorCodes.InvalidRequest, "contextLines must be between 0 and 20.");
        if (maxBytes < 1 || maxBytes > 4_194_304)
            return GitDiffFailure(commandId, ErrorCodes.InvalidRequest, "maxBytes must be between 1 and 4194304.");
        if (pathSpecs.Count > 100 ||
            pathSpecs.Any(spec => string.IsNullOrWhiteSpace(spec) || spec.Length > 512) ||
            pathSpecs.Sum(spec => spec.Length) > 16_384)
        {
            return GitDiffFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                "pathSpecs may contain at most 100 non-empty entries of at most 512 characters and 16384 characters in total.");
        }

        try
        {
            var resolution = await ResolveGitRepositoryRootAsync(path, commandId, cancellationToken);
            if (resolution.Error is not null)
                return GitDiffFailure(commandId, resolution.Error.Code, resolution.Error.Message);

            var repositoryRoot = resolution.Root!;
            var configurationError = await ValidateSafeGitConfigurationAsync(
                repositoryRoot,
                cancellationToken);
            if (configurationError is not null)
                return GitDiffFailure(commandId, configurationError.Code, configurationError.Message);

            var trackedDiscoveryArguments = new List<string>
            {
                "diff",
                "--name-only",
                "-z",
                "--no-renames"
            };
            if (staged)
                trackedDiscoveryArguments.Add("--cached");
            trackedDiscoveryArguments.Add("--");
            trackedDiscoveryArguments.AddRange(pathSpecs);

            var trackedDiscovery = await RunGitAsync(
                repositoryRoot,
                trackedDiscoveryArguments,
                maxStdoutBytes: 2_097_152,
                timeout: TimeSpan.FromSeconds(20),
                cancellationToken);

            if (trackedDiscovery.TimedOut)
                return GitDiffFailure(commandId, ErrorCodes.CommandTimeout, "Git changed-file discovery timed out.");
            if (trackedDiscovery.StartError is not null)
                return GitDiffFailure(commandId, ErrorCodes.GitNotAvailable, "Git is not available on the Windows agent.");
            if (trackedDiscovery.ExitCode != 0)
                return GitDiffFailure(commandId, ErrorCodes.GitCommandFailed, "Git could not enumerate changed files.");
            if (trackedDiscovery.StdoutTruncated)
                return GitDiffFailure(commandId, ErrorCodes.FileTooLarge, "The changed-file list exceeded the allowed response limit.");

            var trackedPaths = ParseNullSeparatedPaths(trackedDiscovery.Stdout)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var authorizedTrackedPaths = trackedPaths
                .Where(relativePath => TryAuthorizeGitRelativePath(repositoryRoot, relativePath, out _))
                .ToList();
            var omittedFiles = trackedPaths.Count - authorizedTrackedPaths.Count;

            if (authorizedTrackedPaths.Count > 200 ||
                authorizedTrackedPaths.Sum(relativePath => relativePath.Length) > 16_384)
            {
                return GitDiffFailure(
                    commandId,
                    ErrorCodes.ResultLimitExceeded,
                    "Too many changed files matched for one bounded Git command. Narrow pathSpecs before requesting a diff.");
            }

            var builder = new StringBuilder();
            var bytesReturned = 0;
            var truncated = false;

            if (authorizedTrackedPaths.Count > 0)
            {
                var arguments = new List<string>
                {
                    "diff",
                    "--no-ext-diff",
                    "--no-textconv",
                    "--no-color",
                    "--no-renames",
                    "--src-prefix=a/",
                    "--dst-prefix=b/",
                    $"--unified={contextLines}"
                };
                if (staged)
                    arguments.Add("--cached");
                arguments.Add("--");
                arguments.AddRange(authorizedTrackedPaths);

                var diffProcess = await RunGitAsync(
                    repositoryRoot,
                    arguments,
                    maxStdoutBytes: maxBytes,
                    timeout: TimeSpan.FromSeconds(30),
                    cancellationToken);

                if (diffProcess.TimedOut)
                    return GitDiffFailure(commandId, ErrorCodes.CommandTimeout, "Git diff timed out.");
                if (diffProcess.StartError is not null)
                    return GitDiffFailure(commandId, ErrorCodes.GitNotAvailable, "Git is not available on the Windows agent.");
                if (diffProcess.ExitCode != 0)
                    return GitDiffFailure(commandId, ErrorCodes.GitCommandFailed, "Git could not produce the requested diff.");

                var boundedDiff = TruncateUtf8(diffProcess.Stdout, maxBytes);
                builder.Append(boundedDiff.Text);
                bytesReturned = boundedDiff.Bytes;
                truncated = diffProcess.StdoutTruncated || boundedDiff.Text.Length < diffProcess.Stdout.Length;
            }

            var untrackedResults = new List<GitUntrackedFileResult>();
            var effectiveIncludeUntracked = includeUntracked && !staged;

            if (effectiveIncludeUntracked && !truncated)
            {
                var untrackedArguments = new List<string>
                {
                    "ls-files",
                    "--others",
                    "--exclude-standard",
                    "-z",
                    "--"
                };
                untrackedArguments.AddRange(pathSpecs);

                var untrackedProcess = await RunGitAsync(
                    repositoryRoot,
                    untrackedArguments,
                    maxStdoutBytes: 2_097_152,
                    timeout: TimeSpan.FromSeconds(20),
                    cancellationToken);

                if (untrackedProcess.TimedOut)
                    return GitDiffFailure(commandId, ErrorCodes.CommandTimeout, "Git untracked-file discovery timed out.");
                if (untrackedProcess.StartError is not null)
                    return GitDiffFailure(commandId, ErrorCodes.GitNotAvailable, "Git is not available on the Windows agent.");
                if (untrackedProcess.ExitCode != 0)
                    return GitDiffFailure(commandId, ErrorCodes.GitCommandFailed, "Git could not enumerate untracked files.");

                if (untrackedProcess.StdoutTruncated)
                {
                    truncated = true;
                }
                else
                {
                    foreach (var relativePath in ParseNullSeparatedPaths(untrackedProcess.Stdout))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var normalizedRelativePath = NormalizeGitPath(relativePath);
                        var inclusion = await AppendUntrackedFileDiffAsync(
                            repositoryRoot,
                            normalizedRelativePath,
                            builder,
                            bytesReturned,
                            maxBytes,
                            cancellationToken);

                        bytesReturned = inclusion.BytesReturned;
                        truncated |= inclusion.ResponseTruncated;
                        if (inclusion.Result is not null)
                            untrackedResults.Add(inclusion.Result);
                        if (inclusion.Omitted)
                            omittedFiles++;

                        if (truncated)
                            break;
                    }
                }
            }

            return new CommandResult<GitDiffResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new GitDiffResult
                {
                    RepositoryRoot = repositoryRoot,
                    Staged = staged,
                    IncludeUntracked = effectiveIncludeUntracked,
                    Diff = builder.ToString(),
                    BytesReturned = bytesReturned,
                    Truncated = truncated,
                    OmittedFiles = omittedFiles,
                    UntrackedFiles = untrackedResults
                }
            };
        }
        catch (OperationCanceledException)
        {
            return GitDiffFailure(commandId, ErrorCodes.CommandCancelled, "The Git diff command was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Git diff failure for command {CommandId}", commandId);
            return GitDiffFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while reading Git diff.");
        }
    }

    internal static List<GitStatusEntry> ParseGitStatusPorcelain(string output)
    {
        var records = output.Split('\0');
        var entries = new List<GitStatusEntry>();

        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.Length < 3)
                continue;

            var status = record[..2];
            var path = record.Length > 3 ? record[3..] : string.Empty;
            string? originalPath = null;

            if ((status[0] is 'R' or 'C' || status[1] is 'R' or 'C') && index + 1 < records.Length)
                originalPath = records[++index];

            entries.Add(new GitStatusEntry
            {
                Path = NormalizeGitPath(path),
                OriginalPath = string.IsNullOrEmpty(originalPath) ? null : NormalizeGitPath(originalPath),
                Status = status,
                IndexStatus = status[0].ToString(),
                WorkTreeStatus = status[1].ToString(),
                IsUntracked = status == "??",
                IsConflict = IsConflictStatus(status)
            });
        }

        return entries;
    }

    internal static string BuildUntrackedPatch(string relativePath, string content)
    {
        var normalizedPath = NormalizeGitPath(relativePath);
        var normalizedContent = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var hasTrailingNewline = normalizedContent.EndsWith('\n');
        var lines = normalizedContent.Split('\n');
        var lineCount = normalizedContent.Length == 0
            ? 0
            : hasTrailingNewline ? lines.Length - 1 : lines.Length;

        var oldPath = QuoteGitDiffPath("a/", normalizedPath);
        var newPath = QuoteGitDiffPath("b/", normalizedPath);
        var builder = new StringBuilder();
        builder.Append("diff --git ").Append(oldPath).Append(' ').Append(newPath).Append('\n');
        builder.Append("new file mode 100644\n");
        builder.Append("--- /dev/null\n");
        builder.Append("+++ ").Append(newPath).Append('\n');

        if (lineCount > 0)
        {
            builder.Append("@@ -0,0 +1,").Append(lineCount).Append(" @@\n");
            for (var index = 0; index < lineCount; index++)
                builder.Append('+').Append(lines[index]).Append('\n');

            if (!hasTrailingNewline)
                builder.Append("\\ No newline at end of file\n");
        }

        return builder.ToString();
    }

    private async Task<(string? Root, CommandError? Error)> ResolveGitRepositoryRootAsync(
        string path,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var process = await RunGitAsync(
            path,
            ["rev-parse", "--show-toplevel"],
            maxStdoutBytes: 65_536,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken);

        if (process.TimedOut)
            return (null, new CommandError(ErrorCodes.CommandTimeout, "Git repository discovery timed out."));
        if (process.StartError is not null)
            return (null, new CommandError(ErrorCodes.GitNotAvailable, "Git is not available on the Windows agent."));
        if (process.ExitCode != 0 || process.StdoutTruncated)
            return (null, new CommandError(ErrorCodes.GitRepositoryNotFound, "The requested path is not inside a supported Git work tree."));

        var rawRoot = process.Stdout.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(rawRoot))
            return (null, new CommandError(ErrorCodes.GitRepositoryNotFound, "The requested path is not inside a supported Git work tree."));

        var policyError = _pathPolicy.AuthorizeReadDirectory(rawRoot, out var normalizedRoot);
        if (policyError is not null)
        {
            _logger.LogWarning(
                "Git repository root authorization failed for command {CommandId}: {ErrorCode}",
                commandId,
                policyError.Code);
            return (null, policyError);
        }

        return (normalizedRoot, null);
    }

    internal static IReadOnlyList<string> BuildGitFilterValidationArguments() =>
    [
        "config",
        "--local",
        "--includes",
        "--name-only",
        "--get-regexp",
        "^filter\\..*\\.(clean|process)$"
    ];

    private async Task<CommandError?> ValidateSafeGitConfigurationAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var process = await RunGitAsync(
            repositoryRoot,
            BuildGitFilterValidationArguments(),
            maxStdoutBytes: 65_536,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken);

        if (process.TimedOut)
            return new CommandError(ErrorCodes.CommandTimeout, "Git configuration validation timed out.");
        if (process.StartError is not null)
            return new CommandError(ErrorCodes.GitNotAvailable, "Git is not available on the Windows agent.");
        if (process.StdoutTruncated)
            return new CommandError(ErrorCodes.GitCommandFailed, "Git filter configuration exceeded the validation limit.");
        if (process.ExitCode == 1 && string.IsNullOrWhiteSpace(process.Stdout))
            return null;
        if (process.ExitCode != 0)
            return new CommandError(ErrorCodes.GitCommandFailed, "Git configuration could not be validated safely.");
        if (!string.IsNullOrWhiteSpace(process.Stdout))
        {
            return new CommandError(
                ErrorCodes.GitCommandFailed,
                "Repositories with executable local Git clean or process filters are not supported by the read-only Git tools.");
        }

        return null;
    }

    private async Task<string?> GetOptionalGitOutputAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var process = await RunGitAsync(
            repositoryRoot,
            arguments,
            maxStdoutBytes: 65_536,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken);

        if (process.TimedOut || process.StartError is not null || process.ExitCode != 0 || process.StdoutTruncated)
            return null;

        var value = process.Stdout.TrimEnd('\r', '\n');
        return value.Length == 0 ? null : value;
    }

    private async Task<UntrackedAppendResult> AppendUntrackedFileDiffAsync(
        string repositoryRoot,
        string relativePath,
        StringBuilder destination,
        int bytesReturned,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(
                repositoryRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new UntrackedAppendResult(null, bytesReturned, ResponseTruncated: false, Omitted: true);
        }

        if (!IsPathWithinRoot(repositoryRoot, fullPath))
            return new UntrackedAppendResult(null, bytesReturned, ResponseTruncated: false, Omitted: true);

        if (!TryAuthorizeGitRelativePath(repositoryRoot, relativePath, out _))
            return new UntrackedAppendResult(null, bytesReturned, ResponseTruncated: false, Omitted: true);

        var policyError = _pathPolicy.AuthorizeReadFile(fullPath, out var normalizedPath);
        if (policyError is not null)
            return new UntrackedAppendResult(null, bytesReturned, ResponseTruncated: false, Omitted: true);

        FileInfo info;
        try
        {
            info = new FileInfo(normalizedPath);
            if (!info.Exists)
                return CreateSkippedUntracked(relativePath, 0, "missing", bytesReturned);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return CreateSkippedUntracked(relativePath, info.Length, "reparse_point", bytesReturned);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CreateSkippedUntracked(relativePath, 0, "unreadable", bytesReturned);
        }

        const long maxUntrackedFileBytes = 1_048_576;
        if (info.Length > maxUntrackedFileBytes)
            return CreateSkippedUntracked(relativePath, info.Length, "file_too_large", bytesReturned);

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(normalizedPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CreateSkippedUntracked(relativePath, info.Length, "unreadable", bytesReturned);
        }

        if (IsBinary(bytes))
            return CreateSkippedUntracked(relativePath, info.Length, "binary", bytesReturned);

        string content;
        try
        {
            (content, _) = DecodeText(bytes);
        }
        catch (ArgumentException)
        {
            return CreateSkippedUntracked(relativePath, info.Length, "unsupported_encoding", bytesReturned);
        }

        var patch = BuildUntrackedPatch(relativePath, content);
        if (destination.Length > 0)
            patch = "\n" + patch;

        var remainingBytes = maxBytes - bytesReturned;
        var patchBytes = LenientUtf8Encoding.GetByteCount(patch);
        if (patchBytes <= remainingBytes)
        {
            destination.Append(patch);
            return new UntrackedAppendResult(
                new GitUntrackedFileResult
                {
                    Path = relativePath,
                    Size = info.Length,
                    Included = true,
                    Truncated = false
                },
                bytesReturned + patchBytes,
                ResponseTruncated: false,
                Omitted: false);
        }

        var truncatedPatch = TruncateUtf8(patch, Math.Max(0, remainingBytes));
        destination.Append(truncatedPatch.Text);
        return new UntrackedAppendResult(
            new GitUntrackedFileResult
            {
                Path = relativePath,
                Size = info.Length,
                Included = truncatedPatch.Bytes > 0,
                Truncated = true,
                Reason = "response_limit"
            },
            bytesReturned + truncatedPatch.Bytes,
            ResponseTruncated: true,
            Omitted: false);
    }

    private static UntrackedAppendResult CreateSkippedUntracked(
        string path,
        long size,
        string reason,
        int bytesReturned) => new(
            new GitUntrackedFileResult
            {
                Path = path,
                Size = size,
                Included = false,
                Truncated = false,
                Reason = reason
            },
            bytesReturned,
            ResponseTruncated: false,
            Omitted: false);

    private bool IsGitStatusEntryAuthorized(string repositoryRoot, GitStatusEntry entry)
    {
        if (!TryAuthorizeGitRelativePath(repositoryRoot, entry.Path, out _))
            return false;

        return entry.OriginalPath is null ||
            TryAuthorizeGitRelativePath(repositoryRoot, entry.OriginalPath, out _);
    }

    private bool TryAuthorizeGitRelativePath(
        string repositoryRoot,
        string relativePath,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(
                repositoryRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathWithinRoot(repositoryRoot, fullPath))
                return false;

            var policyError = _pathPolicy.AuthorizeStat(fullPath, out normalizedPath);
            return policyError is null && IsPathWithinRoot(repositoryRoot, normalizedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static List<string> ParseNullSeparatedPaths(string value) => value
        .Split('\0', StringSplitOptions.RemoveEmptyEntries)
        .Select(NormalizeGitPath)
        .ToList();

    private static void ParseAheadBehind(string? value, out int ahead, out int behind)
    {
        ahead = 0;
        behind = 0;
        if (string.IsNullOrWhiteSpace(value))
            return;

        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            _ = int.TryParse(parts[0], out ahead);
            _ = int.TryParse(parts[1], out behind);
        }
    }

    private static bool IsConflictStatus(string status) => status is
        "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU";

    private static string NormalizeGitPath(string path) => path.Replace('\\', '/');

    private static bool IsPathWithinRoot(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        if (string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase))
            return true;

        return normalizedPath.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteGitDiffPath(string prefix, string path)
    {
        var value = prefix + path;
        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }

    private static (string Text, int Bytes) TruncateUtf8(string value, int maxBytes)
    {
        if (maxBytes <= 0 || value.Length == 0)
            return (string.Empty, 0);

        var builder = new StringBuilder(Math.Min(value.Length, maxBytes));
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
                break;
            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }

        return (builder.ToString(), bytes);
    }

    private static CommandResult<GitStatusResult> GitStatusFailure(
        Guid commandId,
        string code,
        string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };

    private static CommandResult<GitDiffResult> GitDiffFailure(
        Guid commandId,
        string code,
        string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };

    private async Task<GitProcessResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        int maxStdoutBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.pager=cat");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("color.ui=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("submodule.recurse=false");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["LC_ALL"] = "C";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return GitProcessResult.NotStarted("Git process could not be started.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Unable to start Git process.");
            return GitProcessResult.NotStarted("Git process could not be started.");
        }

        var stdoutTask = ReadBoundedOutputAsync(process.StandardOutput.BaseStream, maxStdoutBytes);
        var stderrTask = ReadBoundedOutputAsync(process.StandardError.BaseStream, 65_536);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKillProcess(process);
            await WaitForExitAfterKillAsync(process);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            await WaitForExitAfterKillAsync(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new GitProcessResult(
            timedOut ? -1 : process.ExitCode,
            LenientUtf8Encoding.GetString(stdout.Bytes),
            LenientUtf8Encoding.GetString(stderr.Bytes),
            stdout.Truncated,
            stderr.Truncated,
            timedOut,
            StartError: null);
    }

    private static async Task<BoundedOutput> ReadBoundedOutputAsync(Stream stream, int maxBytes)
    {
        using var destination = new MemoryStream(Math.Min(maxBytes, 65_536));
        var buffer = new byte[8192];
        var truncated = false;
        int read;

        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            var remaining = maxBytes - (int)destination.Length;
            if (remaining > 0)
                destination.Write(buffer, 0, Math.Min(remaining, read));
            if (read > remaining)
                truncated = true;
        }

        return new BoundedOutput(destination.ToArray(), truncated);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch
        {
        }
    }

    private sealed record BoundedOutput(byte[] Bytes, bool Truncated);

    private sealed record GitProcessResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        bool StdoutTruncated,
        bool StderrTruncated,
        bool TimedOut,
        string? StartError)
    {
        public static GitProcessResult NotStarted(string error) => new(
            ExitCode: -1,
            Stdout: string.Empty,
            Stderr: string.Empty,
            StdoutTruncated: false,
            StderrTruncated: false,
            TimedOut: false,
            StartError: error);
    }

    private sealed record UntrackedAppendResult(
        GitUntrackedFileResult? Result,
        int BytesReturned,
        bool ResponseTruncated,
        bool Omitted);

    public async Task<CommandResult<GitRestoreFileResult>> GitRestoreFileAsync(
        string path,
        string pathSpec,
        string? expectedSha256,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await ResolveGitRepositoryRootAsync(path, commandId, cancellationToken);
            if (resolution.Error is not null)
                return new CommandResult<GitRestoreFileResult> { CommandId = commandId, Success = false, Error = resolution.Error };

            var repositoryRoot = resolution.Root!;

            var configurationError = await ValidateSafeGitConfigurationAsync(repositoryRoot, cancellationToken);
            if (configurationError is not null)
                return new CommandResult<GitRestoreFileResult> { CommandId = commandId, Success = false, Error = configurationError };

            if (string.IsNullOrWhiteSpace(pathSpec) ||
                pathSpec.Contains('*') || pathSpec.Contains('?') ||
                pathSpec.Contains('[') || pathSpec.Contains(']') ||
                pathSpec.Contains("..") || pathSpec.Contains('\\') ||
                pathSpec.Contains(':') || pathSpec.StartsWith('/'))
            {
                return new CommandResult<GitRestoreFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.InvalidRequest, "Invalid pathSpec format. Magic, wildcards, absolute path, backslash, colon, and traversal are rejected.")
                };
            }

            var fullFilePath = Path.GetFullPath(Path.Combine(repositoryRoot, pathSpec.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathWithinRoot(repositoryRoot, fullFilePath))
            {
                return new CommandResult<GitRestoreFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.WriteNotAllowed, "Path is outside the repository root.")
                };
            }

            var policyError = _pathPolicy.AuthorizeWriteFile(fullFilePath, out var normalizedFilePath);
            if (policyError is not null)
            {
                return new CommandResult<GitRestoreFileResult> { CommandId = commandId, Success = false, Error = policyError };
            }

            if (File.Exists(normalizedFilePath))
            {
                var attrs = File.GetAttributes(normalizedFilePath);
                if (attrs.HasFlag(FileAttributes.Directory) || attrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    return new CommandResult<GitRestoreFileResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.AccessDenied, "Cannot restore onto a directory, symlink or reparse point.")
                    };
                }
            }
            else if (Directory.Exists(normalizedFilePath))
            {
                return new CommandResult<GitRestoreFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.AccessDenied, "Cannot restore onto a directory.")
                };
            }

            var lsTreeProcess = await RunGitAsync(
                repositoryRoot,
                ["ls-tree", "-z", "HEAD", "--", pathSpec],
                maxStdoutBytes: 65_536,
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken);

            if (lsTreeProcess.TimedOut)
                return new CommandResult<GitRestoreFileResult> { CommandId = commandId, Success = false, Error = new CommandError(ErrorCodes.CommandTimeout, "Git query timed out.") };
            if (lsTreeProcess.StartError is not null)
                return new CommandResult<GitRestoreFileResult> { CommandId = commandId, Success = false, Error = new CommandError(ErrorCodes.GitNotAvailable, lsTreeProcess.StartError) };
            if (lsTreeProcess.ExitCode != 0 || string.IsNullOrWhiteSpace(lsTreeProcess.Stdout))
            {
                return new CommandResult<GitRestoreFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.FileNotFound, "The requested file is not tracked in HEAD.")
                };
            }

            var parts = lsTreeProcess.Stdout.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                return new CommandResult<GitRestoreFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.GitCommandFailed, "Failed to parse Git tracking metadata from HEAD.")
                };
            }

            var mode = parts[0];
            var type = parts[1];
            if (type != "blob" || mode == "120000" || mode == "160000")
            {
                return new CommandResult<GitRestoreFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.AccessDenied, "The tracked object in HEAD is not a regular file (symlinks/submodules are rejected).")
                };
            }

            var checkAttrProcess = await RunGitAsync(
                repositoryRoot,
                ["check-attr", "filter", "--", pathSpec],
                maxStdoutBytes: 65_536,
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken);

            if (checkAttrProcess.ExitCode == 0 && !string.IsNullOrWhiteSpace(checkAttrProcess.Stdout))
            {
                var filterIndex = checkAttrProcess.Stdout.LastIndexOf(':');
                if (filterIndex >= 0)
                {
                    var filterValue = checkAttrProcess.Stdout[(filterIndex + 1)..].Trim();
                    if (filterValue != "unspecified")
                    {
                        return new CommandResult<GitRestoreFileResult>
                        {
                            CommandId = commandId,
                            Success = false,
                            Error = new CommandError(ErrorCodes.AccessDenied, "Files with custom Git filters are rejected.")
                        };
                    }
                }
            }

            var catSizeProcess = await RunGitAsync(
                repositoryRoot,
                ["cat-file", "-s", $"HEAD:{pathSpec}"],
                maxStdoutBytes: 65_536,
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken);

            if (catSizeProcess.ExitCode != 0 || !long.TryParse(catSizeProcess.Stdout.Trim(), out var blobSize))
            {
                return new CommandResult<GitRestoreFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.GitCommandFailed, "Failed to retrieve the blob size from HEAD.")
                };
            }

            if (blobSize > _options.MaxWriteBytes)
            {
                return new CommandResult<GitRestoreFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.FileTooLarge, "The target file size exceeds the allowed MaxWriteBytes limit.")
                };
            }

            var catBlobProcess = await RunGitBytesAsync(
                repositoryRoot,
                ["cat-file", "blob", $"HEAD:{pathSpec}"],
                maxStdoutBytes: (int)_options.MaxWriteBytes + 1024,
                timeout: TimeSpan.FromSeconds(20),
                cancellationToken);

            if (catBlobProcess.ExitCode != 0)
            {
                return new CommandResult<GitRestoreFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.GitCommandFailed, "Failed to retrieve the file content from HEAD.")
                };
            }

            var blobBytes = catBlobProcess.StdoutBytes;

            string? previousSha256 = null;
            if (File.Exists(normalizedFilePath))
            {
                var prevBytes = await File.ReadAllBytesAsync(normalizedFilePath, cancellationToken);
                previousSha256 = ComputeSha256(prevBytes);
            }

            if (expectedSha256 is not null && !string.Equals(expectedSha256, previousSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new CommandResult<GitRestoreFileResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.HashMismatch, "The expected SHA-256 hash does not match the actual hash of the current file.")
                };
            }

            var parentDir = Path.GetDirectoryName(normalizedFilePath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            await File.WriteAllBytesAsync(normalizedFilePath, blobBytes, cancellationToken);

            var currentSha256 = ComputeSha256(blobBytes);
            var changed = previousSha256 != currentSha256;

            return new CommandResult<GitRestoreFileResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new GitRestoreFileResult
                {
                    RepositoryRoot = repositoryRoot,
                    Path = normalizedFilePath,
                    Source = "HEAD",
                    PreviousSha256 = previousSha256,
                    Sha256 = currentSha256,
                    Size = blobBytes.Length,
                    Changed = changed
                }
            };
        }
        catch (OperationCanceledException)
        {
            return new CommandResult<GitRestoreFileResult> { CommandId = commandId, Success = false, Error = new CommandError(ErrorCodes.CommandCancelled, "The Git restore command was cancelled.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Git restore failure for command {CommandId}", commandId);
            return new CommandResult<GitRestoreFileResult> { CommandId = commandId, Success = false, Error = new CommandError(ErrorCodes.InternalError, "An unexpected error occurred while restoring the file.") };
        }
    }

    public Task<CommandResult<GitPushResult>> GitPublishAsync(
        string path,
        string? remote,
        string? branch,
        bool setUpstream,
        int timeoutSeconds,
        int maxOutputBytes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        return GitPublishCoreAsync(path, remote, branch, setUpstream, timeoutSeconds, maxOutputBytes, commandId, cancellationToken);
    }

    private Task<CommandResult<GitPushResult>> GitPublishCoreAsync(
        string path,
        string? remote,
        string? branch,
        bool setUpstream,
        int timeoutSeconds,
        int maxOutputBytes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        return GitPublishCoreRealAsync(path, remote, branch, setUpstream, timeoutSeconds, maxOutputBytes, commandId, cancellationToken);
    }

    public async Task<CommandResult<GitRefreshIndexResult>> GitRefreshIndexAsync(
        string path,
        string pathSpec,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await ResolveGitRepositoryRootAsync(path, commandId, cancellationToken);
            if (resolution.Error is not null)
                return new CommandResult<GitRefreshIndexResult> { CommandId = commandId, Success = false, Error = resolution.Error };

            var repositoryRoot = resolution.Root!;

            var configurationError = await ValidateSafeGitConfigurationAsync(repositoryRoot, cancellationToken);
            if (configurationError is not null)
                return new CommandResult<GitRefreshIndexResult> { CommandId = commandId, Success = false, Error = configurationError };

            if (string.IsNullOrWhiteSpace(pathSpec) ||
                pathSpec.Contains('*') || pathSpec.Contains('?') ||
                pathSpec.Contains('[') || pathSpec.Contains(']') ||
                pathSpec.Contains("..") || pathSpec.Contains('\\') ||
                pathSpec.Contains(':') || pathSpec.StartsWith('/'))
            {
                return new CommandResult<GitRefreshIndexResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.InvalidRequest, "Invalid pathSpec format. Magic, wildcards, absolute path, backslash, colon, and traversal are rejected.")
                };
            }

            var fullFilePath = Path.GetFullPath(Path.Combine(repositoryRoot, pathSpec.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathWithinRoot(repositoryRoot, fullFilePath))
            {
                return new CommandResult<GitRefreshIndexResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.WriteNotAllowed, "Path is outside the repository root.")
                };
            }

            var policyError = _pathPolicy.AuthorizeWriteFile(fullFilePath, out var normalizedFilePath);
            if (policyError is not null)
            {
                return new CommandResult<GitRefreshIndexResult> { CommandId = commandId, Success = false, Error = policyError };
            }

            if (!File.Exists(normalizedFilePath))
            {
                return new CommandResult<GitRefreshIndexResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.FileNotFound, "The requested file does not exist on disk.")
                };
            }

            var attrs = File.GetAttributes(normalizedFilePath);
            if (attrs.HasFlag(FileAttributes.Directory) || attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                return new CommandResult<GitRefreshIndexResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.AccessDenied, "Symlinks, directories and reparse points are rejected.")
                };
            }

            var lsFilesProcess = await RunGitAsync(
                repositoryRoot,
                ["ls-files", "--stage", "--", pathSpec],
                maxStdoutBytes: 65_536,
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken);

            if (lsFilesProcess.ExitCode != 0 || string.IsNullOrWhiteSpace(lsFilesProcess.Stdout))
            {
                return new CommandResult<GitRefreshIndexResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.FileNotFound, "The requested file is not tracked in the repository.")
                };
            }

            var parts = lsFilesProcess.Stdout.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                return new CommandResult<GitRefreshIndexResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.GitCommandFailed, "Failed to parse Git index metadata.")
                };
            }

            var indexMode = parts[0];
            var indexObjectId = parts[1];

            if (indexMode == "120000" || indexMode == "160000")
            {
                return new CommandResult<GitRefreshIndexResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.AccessDenied, "Submodules and symlinks in index are rejected.")
                };
            }

            var lsUnmergedProcess = await RunGitAsync(
                repositoryRoot,
                ["ls-files", "--unmerged", "--", pathSpec],
                maxStdoutBytes: 65_536,
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(lsUnmergedProcess.Stdout))
            {
                return new CommandResult<GitRefreshIndexResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.AccessDenied, "Files with conflict stages are rejected.")
                };
            }

            var checkAttrProcess = await RunGitAsync(
                repositoryRoot,
                ["check-attr", "filter", "--", pathSpec],
                maxStdoutBytes: 65_536,
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken);

            if (checkAttrProcess.ExitCode == 0 && !string.IsNullOrWhiteSpace(checkAttrProcess.Stdout))
            {
                var filterIndex = checkAttrProcess.Stdout.LastIndexOf(':');
                if (filterIndex >= 0)
                {
                    var filterValue = checkAttrProcess.Stdout[(filterIndex + 1)..].Trim();
                    if (filterValue != "unspecified")
                    {
                        return new CommandResult<GitRefreshIndexResult>
                        {
                            CommandId = commandId,
                            Success = false,
                            Error = new CommandError(ErrorCodes.AccessDenied, "Files with custom Git filters are rejected.")
                        };
                    }
                }
            }

            var hashObjectProcess = await RunGitAsync(
                repositoryRoot,
                ["hash-object", "--", pathSpec],
                maxStdoutBytes: 65_536,
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken);

            if (hashObjectProcess.ExitCode != 0 || string.IsNullOrWhiteSpace(hashObjectProcess.Stdout))
            {
                return new CommandResult<GitRefreshIndexResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.GitCommandFailed, "Failed to compute working tree object ID.")
                };
            }

            var workingTreeObjectId = hashObjectProcess.Stdout.Trim();

            if (!string.Equals(workingTreeObjectId, indexObjectId, StringComparison.OrdinalIgnoreCase))
            {
                return new CommandResult<GitRefreshIndexResult>
                {
                    CommandId = commandId,
                    Success = false,
                    Error = new CommandError(ErrorCodes.FileConflict, "The semantic content of the working tree file differs from the index.")
                };
            }

            var diffFilesProcessBefore = await RunGitAsync(
                repositoryRoot,
                ["diff-files", "--quiet", "--", pathSpec],
                maxStdoutBytes: 65_536,
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken);

            bool needsRefresh = diffFilesProcessBefore.ExitCode != 0;
            bool rewrittenFromIndex = false;

            if (needsRefresh)
            {
                var checkoutProcess = await RunGitAsync(
                    repositoryRoot,
                    ["-c", "core.autocrlf=false", "checkout-index", "-f", "--", pathSpec],
                    maxStdoutBytes: 65_536,
                    timeout: TimeSpan.FromSeconds(15),
                    cancellationToken);

                if (checkoutProcess.ExitCode != 0)
                {
                    return new CommandResult<GitRefreshIndexResult>
                    {
                        CommandId = commandId,
                        Success = false,
                        Error = new CommandError(ErrorCodes.GitCommandFailed, $"Failed to checkout file from index: {checkoutProcess.Stderr}")
                    };
                }

                rewrittenFromIndex = true;

                var updateIndexProcess = await RunGitAsync(
                    repositoryRoot,
                    ["-c", "core.autocrlf=false", "update-index", "--really-refresh", "--", pathSpec],
                    maxStdoutBytes: 65_536,
                    timeout: TimeSpan.FromSeconds(15),
                    cancellationToken);
            }

            var diffFilesProcessAfter = await RunGitAsync(
                repositoryRoot,
                ["diff-files", "--quiet", "--", pathSpec],
                maxStdoutBytes: 65_536,
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken);

            bool cleanAfterRefresh = diffFilesProcessAfter.ExitCode == 0;

            return new CommandResult<GitRefreshIndexResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new GitRefreshIndexResult
                {
                    RepositoryRoot = repositoryRoot,
                    Path = normalizedFilePath,
                    IndexObjectId = indexObjectId,
                    WorkingTreeObjectId = workingTreeObjectId,
                    RewrittenFromIndex = rewrittenFromIndex,
                    CleanAfterRefresh = cleanAfterRefresh
                }
            };
        }
        catch (OperationCanceledException)
        {
            return new CommandResult<GitRefreshIndexResult> { CommandId = commandId, Success = false, Error = new CommandError(ErrorCodes.CommandCancelled, "The Git refresh-index command was cancelled.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Git refresh-index failure for command {CommandId}", commandId);
            return new CommandResult<GitRefreshIndexResult> { CommandId = commandId, Success = false, Error = new CommandError(ErrorCodes.InternalError, "An unexpected error occurred while refreshing the index.") };
        }
    }

    private async Task<GitBytesResult> RunGitBytesAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        int maxStdoutBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.pager=cat");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("color.ui=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("submodule.recurse=false");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["LC_ALL"] = "C";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return GitBytesResult.NotStarted("Git process could not be started.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Unable to start Git process.");
            return GitBytesResult.NotStarted("Git process could not be started.");
        }

        var stdoutTask = ReadBoundedOutputAsync(process.StandardOutput.BaseStream, maxStdoutBytes);
        var stderrTask = ReadBoundedOutputAsync(process.StandardError.BaseStream, 65_536);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKillProcess(process);
            await WaitForExitAfterKillAsync(process);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            await WaitForExitAfterKillAsync(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new GitBytesResult(
            timedOut ? -1 : process.ExitCode,
            stdout.Bytes,
            LenientUtf8Encoding.GetString(stderr.Bytes),
            stdout.Truncated,
            stderr.Truncated,
            timedOut,
            StartError: null);
    }

    private sealed record GitBytesResult(
        int ExitCode,
        byte[] StdoutBytes,
        string Stderr,
        bool StdoutTruncated,
        bool StderrTruncated,
        bool TimedOut,
        string? StartError)
    {
        public static GitBytesResult NotStarted(string error) => new(
            ExitCode: -1,
            StdoutBytes: Array.Empty<byte>(),
            Stderr: string.Empty,
            StdoutTruncated: false,
            StderrTruncated: false,
            TimedOut: false,
            StartError: error);
    }
}
