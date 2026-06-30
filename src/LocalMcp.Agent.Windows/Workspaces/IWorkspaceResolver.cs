using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.Workspaces;

public interface IWorkspaceResolver
{
    WorkspaceListResult List();

    WorkspaceResolveOutcome Resolve(
        string alias,
        string? relativePath,
        bool requireWritable);
}

public sealed record WorkspaceResolveOutcome(
    WorkspaceResolveResult? Data,
    CommandError? Error);
