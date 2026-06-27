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
}
