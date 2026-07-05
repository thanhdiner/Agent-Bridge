using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Gateway.Mcp;

public sealed partial class FileSystemTools
{
    private const string GitPushToolName = "git_" + "push";

    [McpServerTool(Name = GitPushToolName, ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true), Description("Publishes local commits from an authorized Git repository to its configured remote. Uses non-interactive Git with prompts disabled, so credentials must already be available on the Windows agent. Requires files:write scope.")]
    public async Task<CallToolResult> GitPushAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent.")] string? deviceId,
        [Description("An absolute directory path inside the Git working tree")] string path,
        [Description("Optional Git remote name. Omit to use the branch upstream.")] string? remote = null,
        [Description("Optional branch name. If provided, remote is required.")] string? branch = null,
        [Description("Whether to set upstream while publishing, equivalent to --set-upstream (default: false)")] bool setUpstream = false,
        [Description("Execution timeout in seconds (default: 120, hard limit: 900)")] int timeoutSeconds = 120,
        [Description("Maximum combined Git output bytes returned (default: 65536, hard limit: 262144)")] int maxOutputBytes = 65_536)
    {
        if (!await AuthorizeScopeAsync("FilesWritePolicy"))
            return CreateErrorResult("FORBIDDEN", "Access denied. Required scope: files:write");
        if (string.IsNullOrWhiteSpace(path))
            return CreateErrorResult("INVALID_REQUEST", "path parameter is required.");
        if (!IsSafeGitRefParameter(remote, allowNullOrWhiteSpace: true))
            return CreateErrorResult("INVALID_REQUEST", "remote must be a safe Git remote name when provided.");
        if (!IsSafeGitRefParameter(branch, allowNullOrWhiteSpace: true))
            return CreateErrorResult("INVALID_REQUEST", "branch must be a safe Git branch name when provided.");
        if (timeoutSeconds is < 10 or > 900)
            return CreateErrorResult("INVALID_REQUEST", "timeoutSeconds must be between 10 and 900.");
        if (maxOutputBytes is < 1 or > 262_144)
            return CreateErrorResult("INVALID_REQUEST", "maxOutputBytes must be between 1 and 262144.");

        var command = new GitPushCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = path,
            Remote = remote,
            Branch = branch,
            SetUpstream = setUpstream,
            TimeoutSeconds = timeoutSeconds,
            MaxOutputBytes = maxOutputBytes
        };

        return await DispatchAsync<GitPushResult>(command, GitPushToolName, deviceId ?? "", GetCancellationToken());
    }

    private static bool IsSafeGitRefParameter(string? value, bool allowNullOrWhiteSpace)
    {
        if (string.IsNullOrWhiteSpace(value))
            return allowNullOrWhiteSpace;

        var trimmed = value.Trim();
        if (trimmed.StartsWith("-", StringComparison.Ordinal) || trimmed.Contains("\0", StringComparison.Ordinal))
            return false;

        return trimmed.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));
    }
}
