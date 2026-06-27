using System.Globalization;
using System.Text.RegularExpressions;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.FileSystem;

public sealed partial class FileSystemExecutor
{
    private const string GitLogFormat = "%x1e%H%x00%h%x00%P%x00%an%x00%ae%x00%aI%x00%s%x00%b%x00";
    private const string GitShowMetadataFormat = "%H%x00%P%x00%an%x00%ae%x00%aI%x00%s%x00%b%x00";

    public async Task<CommandResult<GitLogResult>> GitLogAsync(
        string path,
        int maxCount,
        int skip,
        string? pathSpec,
        string? author,
        string? since,
        string? until,
        bool includeStats,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (maxCount is < 1 or > 100)
            return GitLogFailure(commandId, ErrorCodes.InvalidRequest, "maxCount must be between 1 and 100.");
        if (skip is < 0 or > 1_000_000)
            return GitLogFailure(commandId, ErrorCodes.InvalidRequest, "skip must be between 0 and 1000000.");
        if (!ValidateOptionalBoundedValue(author, 256))
            return GitLogFailure(commandId, ErrorCodes.InvalidRequest, "author must be at most 256 characters.");
        if (!ValidateIsoDate(since))
            return GitLogFailure(commandId, ErrorCodes.InvalidRequest, "since must be a valid ISO date.");
        if (!ValidateIsoDate(until))
            return GitLogFailure(commandId, ErrorCodes.InvalidRequest, "until must be a valid ISO date.");

        try
        {
            var resolution = await ResolveGitRepositoryRootAsync(path, commandId, cancellationToken);
            if (resolution.Error is not null)
                return GitLogFailure(commandId, resolution.Error.Code, resolution.Error.Message);

            var repositoryRoot = resolution.Root!;
            var configurationError = await ValidateSafeGitConfigurationAsync(repositoryRoot, cancellationToken);
            if (configurationError is not null)
                return GitLogFailure(commandId, configurationError.Code, configurationError.Message);

            string? normalizedPathSpec = null;
            if (pathSpec is not null)
            {
                normalizedPathSpec = NormalizeGitPath(pathSpec.Trim());
                if (!IsSafeLiteralHistoryPath(normalizedPathSpec) ||
                    !TryAuthorizeGitRelativePath(repositoryRoot, normalizedPathSpec, out _))
                {
                    return GitLogFailure(
                        commandId,
                        ErrorCodes.InvalidRequest,
                        "pathSpec must be one authorized repository-relative literal path.");
                }
            }

            var arguments = new List<string>
            {
                "log",
                "--no-show-signature",
                "--no-ext-diff",
                "--no-textconv",
                "--no-renames",
                "--diff-merges=first-parent",
                "--date=iso-strict",
                $"--max-count={maxCount + 1}",
                $"--skip={skip}",
                $"--format={GitLogFormat}"
            };

            if (includeStats)
                arguments.Add("--shortstat");
            if (!string.IsNullOrWhiteSpace(author))
                arguments.Add($"--author={author.Trim()}");
            if (!string.IsNullOrWhiteSpace(since))
                arguments.Add($"--since={since.Trim()}");
            if (!string.IsNullOrWhiteSpace(until))
                arguments.Add($"--until={until.Trim()}");
            if (normalizedPathSpec is not null)
            {
                arguments.Add("--");
                arguments.Add(normalizedPathSpec);
            }

            var process = await RunGitAsync(
                repositoryRoot,
                arguments,
                maxStdoutBytes: 4_194_304,
                timeout: TimeSpan.FromSeconds(45),
                cancellationToken);

            if (process.TimedOut)
                return GitLogFailure(commandId, ErrorCodes.CommandTimeout, "Git log timed out.");
            if (process.StartError is not null)
                return GitLogFailure(commandId, ErrorCodes.GitNotAvailable, "Git is not available on the Windows agent.");
            if (process.ExitCode != 0)
                return GitLogFailure(commandId, ErrorCodes.GitCommandFailed, "Git could not read repository history.");

            var parsed = ParseGitLogOutput(process.Stdout, includeStats);
            var countTruncated = parsed.Count > maxCount;
            var commits = countTruncated ? parsed.Take(maxCount).ToList() : parsed;
            var branch = await GetOptionalGitOutputAsync(
                repositoryRoot,
                ["symbolic-ref", "--quiet", "--short", "HEAD"],
                cancellationToken);

            return new CommandResult<GitLogResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new GitLogResult
                {
                    RepositoryRoot = repositoryRoot,
                    Branch = branch,
                    Commits = commits,
                    Truncated = process.StdoutTruncated || countTruncated
                }
            };
        }
        catch (OperationCanceledException)
        {
            return GitLogFailure(commandId, ErrorCodes.CommandCancelled, "The Git log command was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Git log failure for command {CommandId}", commandId);
            return GitLogFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while reading Git history.");
        }
    }

    public async Task<CommandResult<GitShowResult>> GitShowAsync(
        string path,
        string revision,
        IReadOnlyList<string> pathSpecs,
        bool includePatch,
        bool includeStats,
        int contextLines,
        int maxBytes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsSafeRevision(revision))
            return GitShowFailure(commandId, ErrorCodes.InvalidRequest, "revision is invalid or exceeds 256 characters.");
        if (contextLines is < 0 or > 20)
            return GitShowFailure(commandId, ErrorCodes.InvalidRequest, "contextLines must be between 0 and 20.");
        if (maxBytes is < 1 or > 4_194_304)
            return GitShowFailure(commandId, ErrorCodes.InvalidRequest, "maxBytes must be between 1 and 4194304.");
        if (!ValidatePathSpecs(pathSpecs))
        {
            return GitShowFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                "pathSpecs may contain at most 100 non-empty entries of at most 512 characters and 16384 characters in total.");
        }

        try
        {
            var resolution = await ResolveGitRepositoryRootAsync(path, commandId, cancellationToken);
            if (resolution.Error is not null)
                return GitShowFailure(commandId, resolution.Error.Code, resolution.Error.Message);

            var repositoryRoot = resolution.Root!;
            var configurationError = await ValidateSafeGitConfigurationAsync(repositoryRoot, cancellationToken);
            if (configurationError is not null)
                return GitShowFailure(commandId, configurationError.Code, configurationError.Message);

            var resolvedRevision = await ResolveCommitRevisionAsync(repositoryRoot, revision, cancellationToken);
            if (resolvedRevision is null)
                return GitShowFailure(commandId, ErrorCodes.GitCommandFailed, "The requested revision does not resolve to a commit.");

            var metadataProcess = await RunGitAsync(
                repositoryRoot,
                [
                    "show",
                    "-s",
                    "--no-show-signature",
                    "--date=iso-strict",
                    $"--format={GitShowMetadataFormat}",
                    resolvedRevision
                ],
                maxStdoutBytes: 1_048_576,
                timeout: TimeSpan.FromSeconds(20),
                cancellationToken);

            if (metadataProcess.TimedOut)
                return GitShowFailure(commandId, ErrorCodes.CommandTimeout, "Git commit metadata lookup timed out.");
            if (metadataProcess.StartError is not null)
                return GitShowFailure(commandId, ErrorCodes.GitNotAvailable, "Git is not available on the Windows agent.");
            if (metadataProcess.ExitCode != 0 || metadataProcess.StdoutTruncated)
                return GitShowFailure(commandId, ErrorCodes.GitCommandFailed, "Git could not read the requested commit metadata.");

            var commit = ParseGitShowMetadata(metadataProcess.Stdout);
            if (commit is null)
                return GitShowFailure(commandId, ErrorCodes.GitCommandFailed, "Git returned malformed commit metadata.");

            var discoveryArguments = new List<string>
            {
                "show",
                "--format=",
                "--no-ext-diff",
                "--no-textconv",
                "--no-renames",
                "--diff-merges=first-parent",
                "--name-only",
                "-z",
                resolvedRevision,
                "--"
            };
            discoveryArguments.AddRange(pathSpecs);

            var discoveryProcess = await RunGitAsync(
                repositoryRoot,
                discoveryArguments,
                maxStdoutBytes: 2_097_152,
                timeout: TimeSpan.FromSeconds(30),
                cancellationToken);

            if (discoveryProcess.TimedOut)
                return GitShowFailure(commandId, ErrorCodes.CommandTimeout, "Git changed-file discovery timed out.");
            if (discoveryProcess.StartError is not null)
                return GitShowFailure(commandId, ErrorCodes.GitNotAvailable, "Git is not available on the Windows agent.");
            if (discoveryProcess.ExitCode != 0)
                return GitShowFailure(commandId, ErrorCodes.GitCommandFailed, "Git could not enumerate files in the requested commit.");
            if (discoveryProcess.StdoutTruncated)
                return GitShowFailure(commandId, ErrorCodes.FileTooLarge, "The commit file list exceeded the allowed response limit.");

            var changedPaths = ParseGitShowPaths(discoveryProcess.Stdout)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var authorizedPaths = changedPaths
                .Where(relativePath => TryAuthorizeGitRelativePath(repositoryRoot, relativePath, out _))
                .ToList();

            if (authorizedPaths.Count > 200 || authorizedPaths.Sum(relativePath => relativePath.Length) > 16_384)
            {
                return GitShowFailure(
                    commandId,
                    ErrorCodes.ResultLimitExceeded,
                    "Too many authorized files matched one commit. Narrow pathSpecs before requesting git_show.");
            }

            GitShowStatsResult? stats = null;
            if (includeStats && authorizedPaths.Count == 0)
            {
                stats = new GitShowStatsResult();
            }
            else if (includeStats)
            {
                var statsArguments = new List<string>
                {
                    "show",
                    "--format=",
                    "--no-ext-diff",
                    "--no-textconv",
                    "--no-renames",
                    "--diff-merges=first-parent",
                    "--numstat",
                    "-z",
                    resolvedRevision,
                    "--"
                };
                statsArguments.AddRange(authorizedPaths);

                var statsProcess = await RunGitAsync(
                    repositoryRoot,
                    statsArguments,
                    maxStdoutBytes: 2_097_152,
                    timeout: TimeSpan.FromSeconds(30),
                    cancellationToken);

                if (statsProcess.TimedOut)
                    return GitShowFailure(commandId, ErrorCodes.CommandTimeout, "Git commit statistics timed out.");
                if (statsProcess.StartError is not null)
                    return GitShowFailure(commandId, ErrorCodes.GitNotAvailable, "Git is not available on the Windows agent.");
                if (statsProcess.ExitCode != 0 || statsProcess.StdoutTruncated)
                    return GitShowFailure(commandId, ErrorCodes.GitCommandFailed, "Git could not produce commit statistics.");

                stats = ParseGitNumStat(statsProcess.Stdout);
            }

            var patch = string.Empty;
            var bytesReturned = 0;
            var truncated = false;
            if (includePatch && authorizedPaths.Count > 0)
            {
                var patchArguments = new List<string>
                {
                    "show",
                    "--format=",
                    "--no-ext-diff",
                    "--no-textconv",
                    "--no-color",
                    "--no-renames",
                    "--diff-merges=first-parent",
                    $"--unified={contextLines}",
                    resolvedRevision,
                    "--"
                };
                patchArguments.AddRange(authorizedPaths);

                var patchProcess = await RunGitAsync(
                    repositoryRoot,
                    patchArguments,
                    maxStdoutBytes: maxBytes,
                    timeout: TimeSpan.FromSeconds(45),
                    cancellationToken);

                if (patchProcess.TimedOut)
                    return GitShowFailure(commandId, ErrorCodes.CommandTimeout, "Git show patch generation timed out.");
                if (patchProcess.StartError is not null)
                    return GitShowFailure(commandId, ErrorCodes.GitNotAvailable, "Git is not available on the Windows agent.");
                if (patchProcess.ExitCode != 0)
                    return GitShowFailure(commandId, ErrorCodes.GitCommandFailed, "Git could not produce the requested commit patch.");

                var boundedPatch = TruncateUtf8(patchProcess.Stdout, maxBytes);
                patch = boundedPatch.Text;
                bytesReturned = boundedPatch.Bytes;
                truncated = patchProcess.StdoutTruncated || boundedPatch.Text.Length < patchProcess.Stdout.Length;
            }

            return new CommandResult<GitShowResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new GitShowResult
                {
                    RepositoryRoot = repositoryRoot,
                    Revision = resolvedRevision,
                    Commit = commit,
                    Stats = stats,
                    Patch = patch,
                    BytesReturned = bytesReturned,
                    Truncated = truncated
                }
            };
        }
        catch (OperationCanceledException)
        {
            return GitShowFailure(commandId, ErrorCodes.CommandCancelled, "The Git show command was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Git show failure for command {CommandId}", commandId);
            return GitShowFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while reading the Git commit.");
        }
    }

    internal static List<GitLogCommitResult> ParseGitLogOutput(string output, bool includeStats)
    {
        var commits = new List<GitLogCommitResult>();
        foreach (var rawRecord in output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var record = rawRecord.TrimStart('\r', '\n');
            var fields = new string[8];
            var offset = 0;
            var valid = true;

            for (var fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
            {
                var separator = record.IndexOf('\0', offset);
                if (separator < 0)
                {
                    valid = false;
                    break;
                }

                fields[fieldIndex] = record[offset..separator];
                offset = separator + 1;
            }

            if (!valid || string.IsNullOrWhiteSpace(fields[0]))
                continue;
            if (!DateTimeOffset.TryParse(fields[5], CultureInfo.InvariantCulture, DateTimeStyles.None, out var authoredAt))
                continue;

            var (filesChanged, insertions, deletions) = includeStats
                ? ParseGitShortStat(record[offset..])
                : (null, null, null);

            commits.Add(new GitLogCommitResult
            {
                Hash = fields[0],
                ShortHash = fields[1],
                Parents = SplitParents(fields[2]),
                AuthorName = fields[3],
                AuthorEmail = fields[4],
                AuthoredAt = authoredAt,
                Subject = fields[6],
                Body = fields[7].TrimEnd('\r', '\n'),
                FilesChanged = filesChanged,
                Insertions = insertions,
                Deletions = deletions
            });
        }

        return commits;
    }

    internal static GitShowStatsResult ParseGitNumStat(string output)
    {
        var filesChanged = 0;
        var insertions = 0;
        var deletions = 0;

        foreach (var rawEntry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = rawEntry.TrimStart('\r', '\n');
            var firstTab = entry.IndexOf('\t');
            var secondTab = firstTab < 0 ? -1 : entry.IndexOf('\t', firstTab + 1);
            if (firstTab < 0 || secondTab < 0)
                continue;

            filesChanged++;
            if (int.TryParse(entry[..firstTab], NumberStyles.None, CultureInfo.InvariantCulture, out var added))
                insertions += added;
            if (int.TryParse(entry[(firstTab + 1)..secondTab], NumberStyles.None, CultureInfo.InvariantCulture, out var removed))
                deletions += removed;
        }

        return new GitShowStatsResult
        {
            FilesChanged = filesChanged,
            Insertions = insertions,
            Deletions = deletions
        };
    }

    private static (int? FilesChanged, int? Insertions, int? Deletions) ParseGitShortStat(string value)
    {
        return (
            ParseShortStatValue(value, @"(\d+) files? changed"),
            ParseShortStatValue(value, @"(\d+) insertions?\(\+\)"),
            ParseShortStatValue(value, @"(\d+) deletions?\(-\)"));
    }

    private static int? ParseShortStatValue(string value, string pattern)
    {
        var match = Regex.Match(value, pattern, RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var parsed) ? parsed : 0;
    }

    private static GitShowCommitResult? ParseGitShowMetadata(string output)
    {
        var record = output.TrimStart('\r', '\n');
        var fields = new string[7];
        var offset = 0;
        for (var fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
        {
            var separator = record.IndexOf('\0', offset);
            if (separator < 0)
                return null;
            fields[fieldIndex] = record[offset..separator];
            offset = separator + 1;
        }

        if (!DateTimeOffset.TryParse(fields[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var authoredAt))
            return null;

        return new GitShowCommitResult
        {
            Hash = fields[0],
            Parents = SplitParents(fields[1]),
            Author = new GitAuthorResult { Name = fields[2], Email = fields[3] },
            AuthoredAt = authoredAt,
            Subject = fields[5],
            Body = fields[6].TrimEnd('\r', '\n')
        };
    }

    private static IReadOnlyList<string> SplitParents(string parents) => parents
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static List<string> ParseGitShowPaths(string value) => value
        .Split('\0', StringSplitOptions.RemoveEmptyEntries)
        .Select(path => NormalizeGitPath(path.TrimStart('\r', '\n')))
        .Where(path => path.Length > 0)
        .ToList();

    private async Task<string?> ResolveCommitRevisionAsync(
        string repositoryRoot,
        string revision,
        CancellationToken cancellationToken)
    {
        var process = await RunGitAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "--quiet", "--end-of-options", revision.Trim() + "^{commit}"],
            maxStdoutBytes: 65_536,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken);

        if (process.TimedOut || process.StartError is not null || process.ExitCode != 0 || process.StdoutTruncated)
            return null;

        var resolved = process.Stdout.TrimEnd('\r', '\n');
        return resolved.Length is >= 40 and <= 64 && resolved.All(Uri.IsHexDigit) ? resolved : null;
    }

    private static bool ValidatePathSpecs(IReadOnlyList<string> pathSpecs) =>
        pathSpecs.Count <= 100 &&
        pathSpecs.All(spec => !string.IsNullOrWhiteSpace(spec) && spec.Length <= 512 && !spec.Contains('\0')) &&
        pathSpecs.Sum(spec => spec.Length) <= 16_384;

    private static bool ValidateOptionalBoundedValue(string? value, int maxLength) =>
        value is null || (!value.Contains('\0') && value.Length <= maxLength);

    private static bool ValidateIsoDate(string? value) =>
        value is null ||
        (value.Length <= 64 && DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _));

    private static bool IsSafeRevision(string revision) =>
        !string.IsNullOrWhiteSpace(revision) &&
        revision.Length <= 256 &&
        !revision.StartsWith("-", StringComparison.Ordinal) &&
        !revision.Any(char.IsControl) &&
        !revision.Any(char.IsWhiteSpace);

    private static bool IsSafeLiteralHistoryPath(string pathSpec)
    {
        if (string.IsNullOrWhiteSpace(pathSpec) || pathSpec.Length > 512 || Path.IsPathRooted(pathSpec))
            return false;
        if (pathSpec.StartsWith(':') || pathSpec.Contains('\0') || pathSpec.IndexOfAny(['*', '?', '[']) >= 0)
            return false;

        var segments = pathSpec.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(segment => segment is not "." and not "..");
    }

    private static CommandResult<GitLogResult> GitLogFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };

    private static CommandResult<GitShowResult> GitShowFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };
}
