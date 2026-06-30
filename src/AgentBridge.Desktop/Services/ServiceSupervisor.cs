using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LocalMcp.BuildingBlocks.Configuration;

namespace AgentBridge.Desktop.Services;

public sealed class ServiceSupervisor : IAsyncDisposable
{
    public const string DefaultGatewayUrl = "http://127.0.0.1:5227";

    private readonly ServiceBinaryLocator _binaryLocator;
    private readonly LocalDeviceIdentityStore _identityStore;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _gatewayLogGate = new(1, 1);
    private readonly SemaphoreSlim _agentLogGate = new(1, 1);
    private readonly string _logsDirectory;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private Process? _gatewayProcess;
    private Process? _agentProcess;
    private DateTimeOffset _lastGatewayStartUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAgentStartUtc = DateTimeOffset.MinValue;
    private string _deviceId = string.Empty;
    private bool _gatewaySupportsHealthEndpoint;
    private bool _agentDetectedByProcessFallback;
    private bool _stopping;

    public ServiceSupervisor(
        ServiceBinaryLocator? binaryLocator = null,
        LocalDeviceIdentityStore? identityStore = null,
        HttpClient? httpClient = null)
    {
        _binaryLocator = binaryLocator ?? new ServiceBinaryLocator();
        _identityStore = identityStore ?? new LocalDeviceIdentityStore();
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        _logsDirectory = LocalConfigurationPaths.GetLogsDirectory();
        Current = SupervisorSnapshot.Initial(DefaultGatewayUrl, _logsDirectory);
    }

    public event Action<SupervisorSnapshot>? SnapshotChanged;

    public SupervisorSnapshot Current { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_logsDirectory);
            var identity = await _identityStore.LoadOrCreateAsync(cancellationToken);
            _deviceId = identity.DeviceId;
            Publish(Current with
            {
                DeviceId = _deviceId,
                Gateway = StartingStatus("Preparing Gateway…"),
                Agent = StartingStatus("Preparing Windows Agent…")
            });

            await EnsureGatewayAsync(cancellationToken);
            await EnsureAgentAsync(cancellationToken);

            if (_monitorTask is null)
            {
                _monitorCancellation = new CancellationTokenSource();
                _monitorTask = MonitorLoopAsync(_monitorCancellation.Token);
            }
        }
        catch (Exception ex)
        {
            await DesktopLog.WriteAsync("Service supervisor failed to start.", ex, cancellationToken);
            Publish(Current with
            {
                Gateway = ErrorStatus("Supervisor startup failed", ex.Message),
                Agent = ErrorStatus("Supervisor startup failed", ex.Message)
            });
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            Publish(Current with
            {
                Gateway = StartingStatus("Restarting Gateway…"),
                Agent = StartingStatus("Restarting Windows Agent…")
            });

            await StopOwnedAgentAsync(cancellationToken);
            await StopOwnedGatewayAsync(cancellationToken);
            _lastGatewayStartUtc = DateTimeOffset.MinValue;
            _lastAgentStartUtc = DateTimeOffset.MinValue;
            await EnsureGatewayAsync(cancellationToken);
            await EnsureAgentAsync(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> RestartAgentAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (await ProbeAgentAsync(cancellationToken) && !IsAlive(_agentProcess))
            {
                Publish(Current with
                {
                    Agent = ExternalStatus(
                        "Connected externally",
                        "Restart the external Agent manually to apply workspace changes.")
                });
                return false;
            }

            Publish(Current with
            {
                Agent = StartingStatus("Applying workspace policy…")
            });
            await StopOwnedAgentAsync(cancellationToken);
            _lastAgentStartUtc = DateTimeOffset.MinValue;
            await EnsureAgentAsync(cancellationToken);
            return Current.Agent.IsHealthy;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureGatewayAsync(cancellationToken);
            await EnsureAgentAsync(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stopping)
            return;

        _stopping = true;
        _monitorCancellation?.Cancel();
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await StopOwnedAgentAsync(cancellationToken);
            await StopOwnedGatewayAsync(cancellationToken);
            Publish(Current with
            {
                Gateway = StoppedStatus("Stopped by AgentBridge Desktop."),
                Agent = StoppedStatus("Stopped by AgentBridge Desktop.")
            });
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _monitorCancellation?.Dispose();
        _httpClient.Dispose();
        _operationGate.Dispose();
        _gatewayLogGate.Dispose();
        _agentLogGate.Dispose();
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                await RefreshAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await DesktopLog.WriteAsync("Service monitor iteration failed.", ex, cancellationToken);
            }
        }
    }

    private async Task EnsureGatewayAsync(CancellationToken cancellationToken)
    {
        if (await ProbeGatewayAsync(cancellationToken))
        {
            Publish(Current with
            {
                Gateway = !_gatewaySupportsHealthEndpoint
                    ? ExternalStatus(
                        "Running externally",
                        "Legacy Gateway detected on port 5227. Restart it once to enable full health checks.")
                    : IsAlive(_gatewayProcess)
                        ? RunningStatus(
                            "Running",
                            $"Healthy on {DefaultGatewayUrl}",
                            _gatewayProcess!.Id,
                            managed: true)
                        : ExternalStatus(
                            "Running externally",
                            $"A healthy Gateway already owns {DefaultGatewayUrl}.")
            });
            return;
        }

        if (IsAlive(_gatewayProcess))
        {
            Publish(Current with
            {
                Gateway = StartingStatus("Waiting for Gateway health…", _gatewayProcess!.Id)
            });
            return;
        }

        if (DateTimeOffset.UtcNow - _lastGatewayStartUtc < TimeSpan.FromSeconds(10))
        {
            Publish(Current with
            {
                Gateway = ErrorStatus(
                    "Gateway unavailable",
                    "The last start attempt failed. Automatic retry is cooling down.")
            });
            return;
        }

        var target = _binaryLocator.ResolveGateway();
        if (target is null)
        {
            Publish(Current with
            {
                Gateway = ErrorStatus(
                    "Gateway binary missing",
                    "Build LocalMcp.Gateway or package it under services\\gateway.")
            });
            return;
        }

        _lastGatewayStartUtc = DateTimeOffset.UtcNow;
        _gatewayProcess = StartProcess(
            target,
            "gateway.log",
            _gatewayLogGate,
            new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = DefaultGatewayUrl,
                ["ASPNETCORE_ENVIRONMENT"] = "Production"
            });

        Publish(Current with
        {
            Gateway = StartingStatus(
                "Starting Gateway…",
                _gatewayProcess.Id,
                target.DisplayPath)
        });

        if (await WaitUntilAsync(ProbeGatewayAsync, TimeSpan.FromSeconds(15), cancellationToken))
        {
            Publish(Current with
            {
                Gateway = RunningStatus(
                    "Running",
                    $"Healthy on {DefaultGatewayUrl}",
                    _gatewayProcess.Id,
                    managed: true)
            });
            return;
        }

        Publish(Current with
        {
            Gateway = ErrorStatus(
                "Gateway failed health check",
                $"See {Path.Combine(_logsDirectory, "gateway.log")}",
                TryGetProcessId(_gatewayProcess),
                IsAlive(_gatewayProcess))
        });
    }

    private async Task EnsureAgentAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_deviceId))
        {
            Publish(Current with
            {
                Agent = ErrorStatus("Device identity unavailable", "device.json could not be loaded.")
            });
            return;
        }

        if (await ProbeAgentAsync(cancellationToken))
        {
            Publish(Current with
            {
                Agent = _agentDetectedByProcessFallback
                    ? ExternalStatus(
                        "Detected externally",
                        "Legacy Windows Agent process detected. Restart it once to enable connection health.")
                    : IsAlive(_agentProcess)
                        ? RunningStatus(
                            "Connected",
                            $"Registered as {_deviceId}",
                            _agentProcess!.Id,
                            managed: true)
                        : ExternalStatus(
                            "Connected externally",
                            $"An external Agent is registered as {_deviceId}.")
            });
            return;
        }

        if (!await ProbeGatewayAsync(cancellationToken))
        {
            Publish(Current with
            {
                Agent = StoppedStatus("Waiting for a healthy Gateway.")
            });
            return;
        }

        if (IsAlive(_agentProcess))
        {
            Publish(Current with
            {
                Agent = StartingStatus("Connecting to Gateway…", _agentProcess!.Id)
            });
            return;
        }

        if (DateTimeOffset.UtcNow - _lastAgentStartUtc < TimeSpan.FromSeconds(10))
        {
            Publish(Current with
            {
                Agent = ErrorStatus(
                    "Agent unavailable",
                    "The last start attempt failed. Automatic retry is cooling down.")
            });
            return;
        }

        var target = _binaryLocator.ResolveAgent();
        if (target is null)
        {
            Publish(Current with
            {
                Agent = ErrorStatus(
                    "Agent binary missing",
                    "Build LocalMcp.Agent.Windows or package it under services\\agent.")
            });
            return;
        }

        _lastAgentStartUtc = DateTimeOffset.UtcNow;
        _agentProcess = StartProcess(
            target,
            "agent.log",
            _agentLogGate,
            new Dictionary<string, string>
            {
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["Agent__DeviceId"] = _deviceId,
                ["Agent__GatewayUrl"] = DefaultGatewayUrl
            });

        Publish(Current with
        {
            Agent = StartingStatus(
                "Connecting Windows Agent…",
                _agentProcess.Id,
                target.DisplayPath)
        });

        if (await WaitUntilAsync(ProbeAgentAsync, TimeSpan.FromSeconds(15), cancellationToken))
        {
            Publish(Current with
            {
                Agent = RunningStatus(
                    "Connected",
                    $"Registered as {_deviceId}",
                    _agentProcess.Id,
                    managed: true)
            });
            return;
        }

        Publish(Current with
        {
            Agent = ErrorStatus(
                "Agent failed to connect",
                $"See {Path.Combine(_logsDirectory, "agent.log")}",
                TryGetProcessId(_agentProcess),
                IsAlive(_agentProcess))
        });
    }

    private Process StartProcess(
        LaunchTarget target,
        string logFileName,
        SemaphoreSlim logGate,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = target.FileName,
            Arguments = target.Arguments,
            WorkingDirectory = target.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var pair in environment)
            startInfo.Environment[pair.Key] = pair.Value;

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        var logPath = Path.Combine(_logsDirectory, logFileName);
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
                _ = AppendServiceLogAsync(logPath, "OUT", args.Data, logGate);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
                _ = AppendServiceLogAsync(logPath, "ERR", args.Data, logGate);
        };
        process.Exited += (_, _) =>
        {
            _ = AppendServiceLogAsync(
                logPath,
                "EXIT",
                $"Process exited with code {SafeExitCode(process)}.",
                logGate);
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {target.DisplayPath}.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = AppendServiceLogAsync(
            logPath,
            "START",
            $"PID {process.Id}: {target.DisplayPath}",
            logGate);
        return process;
    }

    private async Task<bool> ProbeGatewayAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                $"{DefaultGatewayUrl}/healthz",
                cancellationToken);
            _gatewaySupportsHealthEndpoint = response.IsSuccessStatusCode;
            return response.IsSuccessStatusCode
                || response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> ProbeAgentAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_deviceId))
            return false;

        try
        {
            using var response = await _httpClient.GetAsync(
                $"{DefaultGatewayUrl}/healthz/agent/{Uri.EscapeDataString(_deviceId)}",
                cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                _agentDetectedByProcessFallback = IsLegacyAgentProcessRunning();
                return _agentDetectedByProcessFallback;
            }

            if (!response.IsSuccessStatusCode)
                return false;

            _agentDetectedByProcessFallback = false;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("online", out var online)
                && online.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitUntilAsync(
        Func<CancellationToken, Task<bool>> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await probe(cancellationToken))
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }

        return false;
    }

    private async Task StopOwnedAgentAsync(CancellationToken cancellationToken)
    {
        await StopProcessAsync(_agentProcess, "agent.log", _agentLogGate, cancellationToken);
        _agentProcess = null;
    }

    private async Task StopOwnedGatewayAsync(CancellationToken cancellationToken)
    {
        await StopProcessAsync(_gatewayProcess, "gateway.log", _gatewayLogGate, cancellationToken);
        _gatewayProcess = null;
    }

    private async Task StopProcessAsync(
        Process? process,
        string logFileName,
        SemaphoreSlim logGate,
        CancellationToken cancellationToken)
    {
        if (!IsAlive(process))
        {
            process?.Dispose();
            return;
        }

        var logPath = Path.Combine(_logsDirectory, logFileName);
        await AppendServiceLogAsync(
            logPath,
            "STOP",
            $"Stopping PID {process!.Id}.",
            logGate);

        try
        {
            process.Kill(entireProcessTree: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException)
        {
            await AppendServiceLogAsync(
                logPath,
                "WARN",
                "Timed out while stopping the process.",
                logGate);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool IsLegacyAgentProcessRunning()
    {
        foreach (var process in Process.GetProcessesByName("LocalMcp.Agent.Windows"))
        {
            try
            {
                if (!process.HasExited)
                    return true;
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static bool IsAlive(Process? process)
    {
        if (process is null)
            return false;

        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int? TryGetProcessId(Process? process)
    {
        try
        {
            return process?.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static async Task AppendServiceLogAsync(
        string path,
        string channel,
        string message,
        SemaphoreSlim gate)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await gate.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(
                    path,
                    $"{DateTimeOffset.UtcNow:O} [{channel}] {message}{Environment.NewLine}");
            }
            finally
            {
                gate.Release();
            }
        }
        catch
        {
        }
    }

    private void Publish(SupervisorSnapshot snapshot)
    {
        Current = snapshot with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        SnapshotChanged?.Invoke(Current);
    }

    private static ManagedServiceStatus StartingStatus(
        string detail,
        int? processId = null,
        string? binaryPath = null) => new(
            ManagedServiceState.Starting,
            "Starting",
            binaryPath is null ? detail : $"{detail} {binaryPath}",
            processId,
            false,
            processId is not null);

    private static ManagedServiceStatus RunningStatus(
        string summary,
        string detail,
        int processId,
        bool managed) => new(
            ManagedServiceState.Running,
            summary,
            detail,
            processId,
            true,
            managed);

    private static ManagedServiceStatus ExternalStatus(
        string summary,
        string detail) => new(
            ManagedServiceState.External,
            summary,
            detail,
            null,
            true,
            false);

    private static ManagedServiceStatus ErrorStatus(
        string summary,
        string detail,
        int? processId = null,
        bool managed = false) => new(
            ManagedServiceState.Error,
            summary,
            detail,
            processId,
            false,
            managed);

    private static ManagedServiceStatus StoppedStatus(string detail) => new(
        ManagedServiceState.Stopped,
        "Stopped",
        detail,
        null,
        false,
        false);
}
