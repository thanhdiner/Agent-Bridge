using System;
using System.IO;

namespace AgentBridge.Desktop.Services;

public sealed class ServiceBinaryLocator
{
    private readonly string _baseDirectory;
    private readonly string? _repositoryRoot;

    public ServiceBinaryLocator(string? baseDirectory = null)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        _repositoryRoot = FindRepositoryRoot(_baseDirectory);
    }

    public LaunchTarget? ResolveGateway() => Resolve(
        "AGENTBRIDGE_GATEWAY_PATH",
        "gateway",
        "LocalMcp.Gateway");

    public LaunchTarget? ResolveAgent() => Resolve(
        "AGENTBRIDGE_AGENT_PATH",
        "agent",
        "LocalMcp.Agent.Windows");

    private LaunchTarget? Resolve(
        string environmentVariable,
        string packagedDirectory,
        string projectName)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredTarget = CreateTarget(Path.GetFullPath(configured));
            if (configuredTarget is not null)
                return configuredTarget;
        }

        foreach (var extension in new[] { ".exe", ".dll" })
        {
            var packagedPath = Path.Combine(
                _baseDirectory,
                "services",
                packagedDirectory,
                projectName + extension);
            var packagedTarget = CreateTarget(packagedPath);
            if (packagedTarget is not null)
                return packagedTarget;
        }

        if (_repositoryRoot is null)
            return null;

        var targetFrameworks = projectName == "LocalMcp.Agent.Windows"
            ? new[] { "net8.0-windows", "net8.0" }
            : new[] { "net8.0" };

        foreach (var configuration in new[] { "Release", "Debug" })
        {
            foreach (var targetFramework in targetFrameworks)
            {
                foreach (var extension in new[] { ".exe", ".dll" })
                {
                    var developmentPath = Path.Combine(
                        _repositoryRoot,
                        "src",
                        projectName,
                        "bin",
                        configuration,
                        targetFramework,
                        projectName + extension);
                    var developmentTarget = CreateTarget(developmentPath);
                    if (developmentTarget is not null)
                        return developmentTarget;
                }
            }
        }

        return null;
    }

    private static LaunchTarget? CreateTarget(string path)
    {
        if (!File.Exists(path))
            return null;

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Service binary has no parent directory.");

        return string.Equals(Path.GetExtension(fullPath), ".dll", StringComparison.OrdinalIgnoreCase)
            ? new LaunchTarget(
                "dotnet",
                $"\"{fullPath}\"",
                directory,
                fullPath)
            : new LaunchTarget(
                fullPath,
                string.Empty,
                directory,
                fullPath);
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalMcp.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
