using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

    public LaunchTarget? ResolveAndroidAgent() => Resolve(
        "AGENTBRIDGE_ANDROID_AGENT_PATH",
        "android-agent",
        "LocalMcp.Agent.AndroidAdb");

    public LaunchTarget? ResolveCloudflared(string tunnelName, int? gatewayPort = null)
    {
        var isQuickTunnel = string.IsNullOrWhiteSpace(tunnelName)
            || string.Equals(tunnelName, "quick", StringComparison.OrdinalIgnoreCase);

        var arguments = isQuickTunnel
            ? $"tunnel --url http://127.0.0.1:{gatewayPort ?? 5227}"
            : $"tunnel run {QuoteArgument(tunnelName)}";

        var configured = Environment.GetEnvironmentVariable("AGENTBRIDGE_CLOUDFLARED_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredTarget = CreateExecutableTarget(
                Path.GetFullPath(configured),
                arguments);
            if (configuredTarget is not null)
                return configuredTarget;
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(_baseDirectory, "tools", "cloudflared", "cloudflared.exe"),
                     Path.Combine(_baseDirectory, "cloudflared.exe")
                 })
        {
            var packagedTarget = CreateExecutableTarget(
                candidate,
                arguments);
            if (packagedTarget is not null)
                return packagedTarget;
        }

        var pathTarget = FindOnPath("cloudflared.exe")
            ?? FindOnPath("cloudflared");
        return pathTarget is null
            ? null
            : CreateExecutableTarget(pathTarget, arguments);
    }

    public LaunchTarget? ResolveNgrok(string? customPath, string domain, int port, string? authtoken = null)
    {
        var domainArg = !string.IsNullOrWhiteSpace(domain) ? $"--url={QuoteArgument(domain)} " : string.Empty;
        var tokenArg = !string.IsNullOrWhiteSpace(authtoken) ? $"--authtoken={QuoteArgument(authtoken)} " : string.Empty;
        var arguments = $"http {tokenArg}{domainArg}{port}";

        var configured = !string.IsNullOrWhiteSpace(customPath)
            ? customPath
            : Environment.GetEnvironmentVariable("AGENTBRIDGE_NGROK_PATH");

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredTarget = CreateExecutableTarget(Path.GetFullPath(configured), arguments);
            if (configuredTarget is not null)
                return configuredTarget;
        }

        var candidates = new List<string>
        {
            Path.Combine(_baseDirectory, "tools", "ngrok", "ngrok.exe"),
            Path.Combine(_baseDirectory, "ngrok.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ngrok", "ngrok.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ngrok", "ngrok.exe")
        };

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roamingNvm = Path.Combine(appData, "Roaming", "nvm");
        if (Directory.Exists(roamingNvm))
        {
            try
            {
                candidates.AddRange(Directory.GetFiles(roamingNvm, "ngrok.exe", SearchOption.AllDirectories));
            }
            catch
            {
            }
        }

        var localNvm = Path.Combine(appData, "nvm");
        if (Directory.Exists(localNvm))
        {
            try
            {
                candidates.AddRange(Directory.GetFiles(localNvm, "ngrok.exe", SearchOption.AllDirectories));
            }
            catch
            {
            }
        }

        foreach (var candidate in candidates)
        {
            var target = CreateExecutableTarget(candidate, arguments);
            if (target is not null)
                return target;
        }

        var pathTarget = FindOnPath("ngrok.exe") ?? FindOnPath("ngrok");
        return pathTarget is null
            ? null
            : CreateExecutableTarget(pathTarget, arguments);
    }

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

    private static LaunchTarget? CreateExecutableTarget(string path, string arguments)
    {
        if (!File.Exists(path))
            return null;

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Executable has no parent directory.");

        return new LaunchTarget(
            fullPath,
            arguments,
            directory,
            $"{fullPath} {arguments}");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
    }

    private static string QuoteArgument(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

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
