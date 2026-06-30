using LocalMcp.Agent.Windows.Security;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;
using Microsoft.Extensions.Options;

namespace LocalMcp.Agent.Windows.Workspaces;

public sealed class WorkspaceResolver : IWorkspaceResolver
{
    private readonly IReadOnlyDictionary<string, RegisteredWorkspace> _workspaces;
    private readonly FileAccessOptions _fileAccessOptions;

    public WorkspaceResolver(
        IOptions<WorkspaceOptions> workspaceOptions,
        IOptions<FileAccessOptions> fileAccessOptions)
    {
        ArgumentNullException.ThrowIfNull(workspaceOptions);
        ArgumentNullException.ThrowIfNull(fileAccessOptions);

        _fileAccessOptions = fileAccessOptions.Value;
        var workspaces = new Dictionary<string, RegisteredWorkspace>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in workspaceOptions.Value.Aliases)
        {
            var alias = pair.Key.Trim();
            var definition = pair.Value;
            var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(definition.Path));

            workspaces.Add(alias, new RegisteredWorkspace(
                alias,
                rootPath,
                definition.Writable,
                string.IsNullOrWhiteSpace(definition.Description)
                    ? null
                    : definition.Description.Trim()));
        }

        _workspaces = workspaces;
    }

    public WorkspaceListResult List()
    {
        var items = _workspaces.Values
            .OrderBy(workspace => workspace.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(workspace =>
            {
                var allowed = IsWithinAny(workspace.RootPath, _fileAccessOptions.AllowedRoots);
                var writable = workspace.Writable &&
                    IntersectsAny(workspace.RootPath, _fileAccessOptions.WritableRoots);

                return new WorkspaceInfo
                {
                    Alias = workspace.Alias,
                    RootPath = workspace.RootPath,
                    Description = workspace.Description,
                    Available = Directory.Exists(workspace.RootPath),
                    Allowed = allowed,
                    Writable = allowed && writable
                };
            })
            .ToArray();

        return new WorkspaceListResult { Workspaces = items };
    }

    public WorkspaceResolveOutcome Resolve(
        string alias,
        string? relativePath,
        bool requireWritable)
    {
        var normalizedAlias = alias?.Trim() ?? string.Empty;
        if (!_workspaces.TryGetValue(normalizedAlias, out var workspace))
        {
            return Failure(
                ErrorCodes.WorkspaceNotFound,
                $"Workspace alias '{normalizedAlias}' is not configured.");
        }

        if (!IsWithinAny(workspace.RootPath, _fileAccessOptions.AllowedRoots))
        {
            return Failure(
                ErrorCodes.WorkspaceNotAllowed,
                $"Workspace '{workspace.Alias}' is outside the configured allowed roots.");
        }

        if (requireWritable && !workspace.Writable)
        {
            return Failure(
                ErrorCodes.WorkspaceReadOnly,
                $"Workspace '{workspace.Alias}' is read-only.");
        }

        var requestedRelativePath = relativePath ?? string.Empty;
        if (requestedRelativePath.Length > 32_768 ||
            requestedRelativePath.Any(char.IsControl) ||
            Path.IsPathRooted(requestedRelativePath))
        {
            return Failure(
                ErrorCodes.WorkspacePathInvalid,
                "relativePath must be a relative path of at most 32768 characters without control characters.");
        }

        string absolutePath;
        try
        {
            absolutePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Path.Combine(workspace.RootPath, requestedRelativePath)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(ErrorCodes.WorkspacePathInvalid, "relativePath is not a valid Windows path.");
        }

        if (!PathPolicy.IsSubdirectoryOf(absolutePath, workspace.RootPath))
        {
            return Failure(
                ErrorCodes.WorkspacePathOutsideRoot,
                "relativePath escapes the selected workspace root.");
        }

        var effectiveWritable = workspace.Writable &&
            IsWithinAny(absolutePath, _fileAccessOptions.WritableRoots);
        if (requireWritable && !effectiveWritable)
        {
            return Failure(
                ErrorCodes.WorkspaceReadOnly,
                $"Resolved path in workspace '{workspace.Alias}' is outside its writable roots.");
        }

        var fileExists = File.Exists(absolutePath);
        var directoryExists = Directory.Exists(absolutePath);
        var normalizedRelativePath = string.Equals(
            absolutePath,
            workspace.RootPath,
            StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : Path.GetRelativePath(workspace.RootPath, absolutePath);

        return new WorkspaceResolveOutcome(
            new WorkspaceResolveResult
            {
                Alias = workspace.Alias,
                RootPath = workspace.RootPath,
                RelativePath = normalizedRelativePath,
                AbsolutePath = absolutePath,
                Writable = effectiveWritable,
                Exists = fileExists || directoryExists,
                EntryType = fileExists ? "file" : directoryExists ? "directory" : "missing"
            },
            null);
    }

    private static bool IsWithinAny(string path, IEnumerable<string> configuredRoots)
    {
        foreach (var configuredRoot in configuredRoots)
        {
            if (string.IsNullOrWhiteSpace(configuredRoot))
                continue;

            try
            {
                if (PathPolicy.IsSubdirectoryOf(path, configuredRoot))
                    return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Invalid roots are rejected by startup validation. Ignore defensively here.
            }
        }

        return false;
    }

    private static bool IntersectsAny(string workspaceRoot, IEnumerable<string> configuredRoots)
    {
        foreach (var configuredRoot in configuredRoots)
        {
            if (string.IsNullOrWhiteSpace(configuredRoot))
                continue;

            try
            {
                if (PathPolicy.IsSubdirectoryOf(workspaceRoot, configuredRoot)
                    || PathPolicy.IsSubdirectoryOf(configuredRoot, workspaceRoot))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Invalid roots are rejected by startup validation. Ignore defensively here.
            }
        }

        return false;
    }

    private static WorkspaceResolveOutcome Failure(string code, string message) =>
        new(null, new CommandError(code, message));

    private sealed record RegisteredWorkspace(
        string Alias,
        string RootPath,
        bool Writable,
        string? Description);
}
