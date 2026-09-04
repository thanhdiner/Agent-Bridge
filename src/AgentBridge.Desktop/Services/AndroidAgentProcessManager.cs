using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LocalMcp.BuildingBlocks.Configuration;

namespace AgentBridge.Desktop.Services;

internal sealed class AndroidAgentProcessManager : IDisposable
{
    private readonly ServiceBinaryLocator _binaryLocator = new();
    private readonly InternalTokenStore _tokenStore = new();
    private readonly ChildProcessJob _childProcessJob = ChildProcessJob.Create($"AgentBridge.Desktop.Android.{Environment.ProcessId}");
    private readonly SemaphoreSlim _logGate = new(1, 1);
    private Process? _process;
    private bool _disposed;

    public event Action? StateChanged;

    public bool IsRunning
    {
        get
        {
            try
            {
                return _process is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public int? ProcessId => IsRunning ? _process?.Id : null;

    public async Task StartAsync(string adbPath, string serial, string gatewayUrl, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null)
            Stop();

        var target = _binaryLocator.ResolveAndroidAgent()
            ?? throw new InvalidOperationException("Android Agent binary is missing. Build LocalMcp.Agent.AndroidAdb or package it under services\\android-agent.");
        var token = await _tokenStore.LoadOrCreateAsync(cancellationToken);
        var startInfo = new ProcessStartInfo
        {
            FileName = target.FileName,
            Arguments = target.Arguments,
            WorkingDirectory = target.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var pair in BuildEnvironment(adbPath, serial, gatewayUrl, token))
            startInfo.Environment[pair.Key] = pair.Value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => AppendLog("OUT", args.Data);
        process.ErrorDataReceived += (_, args) => AppendLog("ERR", args.Data);
        process.Exited += (_, _) => StateChanged?.Invoke();
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Android Agent could not be started.");
            if (!_childProcessJob.TryAssign(process, out var assignmentError))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException($"Could not attach Android Agent to the desktop lifecycle: {assignmentError}");
            }

            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            StateChanged?.Invoke();

            await Task.Delay(800, cancellationToken);
            if (process.HasExited)
                throw new InvalidOperationException($"Android Agent exited during startup. Open {GetLogPath()} for details.");
        }
        catch
        {
            process.Dispose();
            if (ReferenceEquals(_process, process))
                _process = null;
            StateChanged?.Invoke();
            throw;
        }
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
            StateChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _childProcessJob.Dispose();
        _logGate.Dispose();
    }

    private static IReadOnlyDictionary<string, string> BuildEnvironment(
        string adbPath,
        string serial,
        string gatewayUrl,
        string token) => new Dictionary<string, string>
    {
        ["DOTNET_ENVIRONMENT"] = "Production",
        ["AGENTBRIDGE_MANAGED_RUNTIME"] = "1",
        ["AndroidAdb__AdbPath"] = adbPath,
        ["AndroidAdb__Serial"] = serial,
        ["AndroidAdb__GatewayUrl"] = gatewayUrl,
        ["AgentSecurity__AuthenticationEnabled"] = "true",
        ["AgentSecurity__TokenEnvironmentVariable"] = InternalTokenStore.TokenEnvironmentVariable,
        [InternalTokenStore.TokenEnvironmentVariable] = token
    };

    private void AppendLog(string stream, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        _ = AppendLogAsync(stream, line);
    }

    private async Task AppendLogAsync(string stream, string line)
    {
        try
        {
            var path = GetLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await _logGate.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(path, $"{DateTimeOffset.UtcNow:O} [{stream}] {line}{Environment.NewLine}");
            }
            finally
            {
                _logGate.Release();
            }
        }
        catch
        {
        }
    }

    private static string GetLogPath() => Path.Combine(LocalConfigurationPaths.GetLogsDirectory(), "android-agent.log");
}
