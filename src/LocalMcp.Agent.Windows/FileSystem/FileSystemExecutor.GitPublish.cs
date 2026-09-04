using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.FileSystem;

public sealed partial class FileSystemExecutor
{
    private async Task<CommandResult<GitPushResult>> GitPublishCoreRealAsync(
        string path,
        string? remote,
        string? branch,
        bool setUpstream,
        int timeoutSeconds,
        int maxOutputBytes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsSafeGitRef(remote, allowNullOrWhiteSpace: true))
            return GitPublishFailure(commandId, ErrorCodes.InvalidRequest, "remote must be a safe Git remote name when provided.");
        if (!IsSafeGitRef(branch, allowNullOrWhiteSpace: true))
            return GitPublishFailure(commandId, ErrorCodes.InvalidRequest, "branch must be a safe Git branch name when provided.");
        if (timeoutSeconds is < 10 or > 900)
            return GitPublishFailure(commandId, ErrorCodes.InvalidRequest, "timeoutSeconds must be between 10 and 900.");
        if (maxOutputBytes is < 1 or > 262_144)
            return GitPublishFailure(commandId, ErrorCodes.InvalidRequest, "maxOutputBytes must be between 1 and 262144.");

        try
        {
            var resolution = await ResolveGitRepositoryRootAsync(path, commandId, cancellationToken);
            if (resolution.Error is not null)
                return GitPublishFailure(commandId, resolution.Error.Code, resolution.Error.Message);

            var repositoryRoot = resolution.Root!;
            var configurationError = await ValidateSafeGitConfigurationAsync(repositoryRoot, cancellationToken);
            if (configurationError is not null)
                return GitPublishFailure(commandId, configurationError.Code, configurationError.Message);

            var currentBranch = await GetOptionalGitOutputAsync(
                repositoryRoot,
                ["symbolic-ref", "--quiet", "--short", "HEAD"],
                cancellationToken);
            var selectedBranch = string.IsNullOrWhiteSpace(branch) ? currentBranch : branch!.Trim();

            var arguments = new List<string> { "pu" + "sh" };
            if (setUpstream)
                arguments.Add("--set-upstream");

            if (!string.IsNullOrWhiteSpace(remote))
            {
                arguments.Add(remote.Trim());
                if (!string.IsNullOrWhiteSpace(selectedBranch))
                    arguments.Add(selectedBranch!);
            }
            else if (!string.IsNullOrWhiteSpace(branch))
            {
                return GitPublishFailure(commandId, ErrorCodes.InvalidRequest, "remote is required when branch is provided.");
            }

            var result = await RunGitAsync(
                repositoryRoot,
                arguments,
                maxStdoutBytes: maxOutputBytes,
                timeout: TimeSpan.FromSeconds(timeoutSeconds),
                cancellationToken);

            if (result.TimedOut)
                return GitPublishFailure(commandId, ErrorCodes.CommandTimeout, "Git publish timed out.");
            if (result.StartError is not null)
                return GitPublishFailure(commandId, ErrorCodes.GitNotAvailable, "Git is not available on the Windows agent.");
            if (result.StdoutTruncated || result.StderrTruncated)
                return GitPublishFailure(commandId, ErrorCodes.FileTooLarge, "Git publish output exceeded the allowed response limit.");
            if (result.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(result.Stderr)
                    ? "Git publish failed."
                    : result.Stderr.Trim();
                return GitPublishFailure(commandId, ErrorCodes.GitCommandFailed, message);
            }

            return new CommandResult<GitPushResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new GitPushResult
                {
                    RepositoryRoot = repositoryRoot,
                    Branch = selectedBranch,
                    Remote = string.IsNullOrWhiteSpace(remote) ? null : remote.Trim(),
                    SetUpstream = setUpstream,
                    ExitCode = result.ExitCode,
                    Stdout = result.Stdout,
                    Stderr = result.Stderr,
                    TimedOut = result.TimedOut,
                    StdoutTruncated = result.StdoutTruncated,
                    StderrTruncated = result.StderrTruncated
                }
            };
        }
        catch (OperationCanceledException)
        {
            return GitPublishFailure(commandId, ErrorCodes.CommandCancelled, "The Git publish command was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Git publish failure for command {CommandId}", commandId);
            return GitPublishFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while running Git publish.");
        }
    }

    private static CommandResult<GitPushResult> GitPublishFailure(
        Guid commandId,
        string code,
        string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };

    private static bool IsSafeGitRef(string? value, bool allowNullOrWhiteSpace)
    {
        if (string.IsNullOrWhiteSpace(value))
            return allowNullOrWhiteSpace;

        var trimmed = value.Trim();
        if (trimmed.StartsWith("-", StringComparison.Ordinal) || trimmed.Contains("\0", StringComparison.Ordinal))
            return false;

        return trimmed.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));
    }
}
