using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LocalMcp.BuildingBlocks.Configuration;

namespace AgentBridge.Desktop.Services;

public sealed class ServiceSupervisor : IAsyncDisposable
{
    public const string DefaultGatewayUrl = "http://127.0.0.1:5227";
    public const string DefaultTunnelName = "localmcp";

    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ForcedStopTimeout = TimeSpan.FromSeconds(5);

    private readonly ServiceBinaryLocator _binaryLocator;
    private readonly LocalDeviceIdentityStore _identityStore;
    private readonly InternalTokenStore _tokenStore;
    private readonly HttpClient _httpClient;
    private readonly RestartBackoff _gatewayBackoff = new();
    private readonly RestartBackoff _agentBackoff = new();
    private readonly RestartBackoff _tunnelBackoff = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _gatewayLogGate = new(1, 1);
    private readonly SemaphoreSlim _agentLogGate = new(1, 1);
    private readonly SemaphoreSlim _tunnelLogGate = new(1, 1);
    private readonly ChildProcessJob _childProcessJob = ChildProcessJob.Create($"AgentBridge.Desktop.Children.{Environment.ProcessId}");
    private readonly string _logsDirectory;
    private readonly string _gatewayUrl;
    private readonly string _tunnelName;
    private readonly int _gatewayPort;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private Process? _gatewayProcess;
    private Process? _agentProcess;
    private Process? _tunnelProcess;
    private string _deviceId = string.Empty;
    private string _internalToken = string.Empty;
    private bool _stopping;

    public ServiceSupervisor(
        ServiceBinaryLocator? binaryLocator = null,
        LocalDeviceIdentityStore? identityStore = null,
        InternalTokenStore? tokenStore = null,
        HttpClient? httpClient = null,
        string? gatewayUrl = null)
    {
        _binaryLocator = binaryLocator ?? new ServiceBinaryLocator();
        _identityStore = identityStore ?? new LocalDeviceIdentityStore();
        _tokenStore = tokenStore ?? new InternalTokenStore();
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        _logsDirectory = LocalConfigurationPaths.GetLogsDirectory();
        _tunnelName = NormalizeTunnelName(
            Environment.GetEnvironmentVariable("AGENTBRIDGE_TUNNEL_NAME")
            ?? DefaultTunnelName);

        var configuredGatewayUrl = gatewayUrl
            ?? Environment.GetEnvironmentVariable("AGENTBRIDGE_GATEWAY_URL")
            ?? DefaultGatewayUrl;
        if (!TryNormalizeGatewayUrl(
                configuredGatewayUrl,
                out _gatewayUrl,
                out _gatewayPort))
        {
            throw new ArgumentException(
                "Managed Gateway URL must be an absolute loopback HTTP URL.",
                nameof(gatewayUrl));
        }
        Current = SupervisorSnapshot.Initial(_gatewayUrl, _logsDirectory);
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
            _internalToken = await _tokenStore.LoadOrCreateAsync(cancellationToken);

            Publish(Current with
            {
                DeviceId = _deviceId,
                Gateway = StartingStatus("Preparing Gateway..."),
                Agent = StartingStatus("Preparing Windows Agent..."),
                Tunnel = StartingStatus("Preparing Cloudflare Tunnel...")
            });

            await EnsureGatewayAsync(cancellationToken);
            await EnsureAgentAsync(cancellationToken);
            await EnsureTunnelAsync(cancellationToken);

            if (_monitorTask is null)
            {
                _monitorCancellation = new CancellationTokenSource();
                _monitorTask = MonitorLoopAsync(_monitorCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await DesktopLog.WriteAsync("Service supervisor failed to start.", ex, cancellationToken);
            Publish(Current with
            {
                Gateway = ErrorStatus("Supervisor startup failed", ex.Message),
                Agent = ErrorStatus("Supervisor startup failed", ex.Message),
                Tunnel = ErrorStatus("Supervisor startup failed", ex.Message)
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
                Gateway = StartingStatus("Restarting Gateway..."),
                Agent = StartingStatus("Restarting Windows Agent..."),
                Tunnel = StartingStatus("Restarting Cloudflare Tunnel...")
            });

            await StopOwnedTunnelAsync(cancellationToken);
            await StopOwnedAgentAsync(cancellationToken);
            await StopOwnedGatewayAsync(cancellationToken);
            _gatewayBackoff.Reset();
            _agentBackoff.Reset();
            _tunnelBackoff.Reset();
            await EnsureGatewayAsync(cancellationToken);
            await EnsureAgentAsync(cancellationToken);
            await EnsureTunnelAsync(cancellationToken);
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
                Agent = StartingStatus("Applying workspace policy...")
            });
            await StopOwnedAgentAsync(cancellationToken);
            _agentBackoff.Reset();
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
            await EnsureTunnelAsync(cancellationToken);
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
            await StopOwnedTunnelAsync(cancellationToken);
            await StopOwnedAgentAsync(cancellationToken);
            await StopOwnedGatewayAsync(cancellationToken);
            Publish(Current with
            {
                Gateway = StoppedStatus("Stopped by AgentBridge Desktop."),
                Agent = StoppedStatus("Stopped by AgentBridge Desktop."),
                Tunnel = StoppedStatus("Stopped by AgentBridge Desktop.")
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
        _tunnelLogGate.Dispose();
        _childProcessJob.Dispose();
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
        await CaptureExitedGatewayAsync();

        var probe = await ProbeGatewayAsync(cancellationToken);
        if (probe == GatewayProbeState.AgentBridge)
        {
            if (IsAlive(_gatewayProcess))
            {
                _gatewayBackoff.ObserveHealthy(DateTimeOffset.UtcNow);
                Publish(Current with
                {
                    Gateway = RunningStatus(
                        "Running",
                        $"Healthy on {_gatewayUrl}",
                        _gatewayProcess!.Id,
                        managed: true)
                });
            }
            else
            {
                Publish(Current with
                {
                    Gateway = ExternalStatus(
                        "Running externally",
                        $"A verified AgentBridge Gateway already owns {_gatewayUrl}.")
                });
            }

            return;
        }

        if (IsAlive(_gatewayProcess))
        {
            Publish(Current with
            {
                Gateway = StartingStatus("Waiting for Gateway health...", _gatewayProcess!.Id)
            });
            return;
        }

        if (probe == GatewayProbeState.ForeignService || IsGatewayPortInUse())
        {
            Publish(Current with
            {
                Gateway = ErrorStatus(
                    $"Port {_gatewayPort} is occupied",
                    $"Another application owns TCP port {_gatewayPort}. Close it, then restart AgentBridge.")
            });
            return;
        }

        if (!_gatewayBackoff.CanStart(DateTimeOffset.UtcNow, out var remaining))
        {
            Publish(Current with
            {
                Gateway = ErrorStatus(
                    "Gateway restart cooling down",
                    $"Automatic retry in {FormatSeconds(remaining)} seconds.")
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
                    "Publish it under services\\gateway or build LocalMcp.Gateway.")
            });
            return;
        }

        try
        {
            _gatewayProcess = StartProcess(
                target,
                "gateway.log",
                _gatewayLogGate,
                BuildGatewayEnvironment());
            _gatewayBackoff.RecordStarted(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            var delay = _gatewayBackoff.RecordFailure(DateTimeOffset.UtcNow);
            await DesktopLog.WriteAsync("Gateway process failed to start.", ex, cancellationToken);
            Publish(Current with
            {
                Gateway = ErrorStatus(
                    "Gateway failed to start",
                    $"Retrying in {FormatSeconds(delay)} seconds. {ex.Message}")
            });
            return;
        }

        Publish(Current with
        {
            Gateway = StartingStatus(
                "Starting Gateway...",
                _gatewayProcess.Id,
                target.DisplayPath)
        });

        var healthy = await WaitUntilAsync(
            async token => await ProbeGatewayAsync(token) == GatewayProbeState.AgentBridge,
            TimeSpan.FromSeconds(15),
            cancellationToken);

        if (healthy)
        {
            Publish(Current with
            {
                Gateway = RunningStatus(
                    "Running",
                    $"Healthy on {_gatewayUrl}",
                    _gatewayProcess.Id,
                    managed: true)
            });
            return;
        }

        var processId = TryGetProcessId(_gatewayProcess);
        await StopOwnedGatewayAsync(cancellationToken);
        var retryDelay = _gatewayBackoff.RecordFailure(DateTimeOffset.UtcNow);
        Publish(Current with
        {
            Gateway = ErrorStatus(
                "Gateway failed health check",
                $"Retrying in {FormatSeconds(retryDelay)} seconds. See {Path.Combine(_logsDirectory, "gateway.log")}",
                processId)
        });
    }

    private async Task EnsureAgentAsync(CancellationToken cancellationToken)
    {
        await CaptureExitedAgentAsync();

        if (string.IsNullOrWhiteSpace(_deviceId) || string.IsNullOrWhiteSpace(_internalToken))
        {
            Publish(Current with
            {
                Agent = ErrorStatus("Runtime identity unavailable", "Device identity or internal token is missing.")
            });
            return;
        }

        if (await ProbeAgentAsync(cancellationToken))
        {
            if (IsAlive(_agentProcess))
            {
                _agentBackoff.ObserveHealthy(DateTimeOffset.UtcNow);
                Publish(Current with
                {
                    Agent = RunningStatus(
                        "Connected",
                        $"Registered as {_deviceId}",
                        _agentProcess!.Id,
                        managed: true)
                });
            }
            else
            {
                Publish(Current with
                {
                    Agent = ExternalStatus(
                        "Connected externally",
                        $"An external Agent is registered as {_deviceId}.")
                });
            }

            return;
        }

        if (await ProbeGatewayAsync(cancellationToken) != GatewayProbeState.AgentBridge)
        {
            Publish(Current with
            {
                Agent = StoppedStatus("Waiting for a verified AgentBridge Gateway.")
            });
            return;
        }

        if (IsAlive(_agentProcess))
        {
            Publish(Current with
            {
                Agent = StartingStatus("Connecting to Gateway...", _agentProcess!.Id)
            });
            return;
        }

        if (!_agentBackoff.CanStart(DateTimeOffset.UtcNow, out var remaining))
        {
            Publish(Current with
            {
                Agent = ErrorStatus(
                    "Agent restart cooling down",
                    $"Automatic retry in {FormatSeconds(remaining)} seconds.")
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
                    "Publish it under services\\agent or build LocalMcp.Agent.Windows.")
            });
            return;
        }

        try
        {
            _agentProcess = StartProcess(
                target,
                "agent.log",
                _agentLogGate,
                BuildAgentEnvironment());
            _agentBackoff.RecordStarted(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            var delay = _agentBackoff.RecordFailure(DateTimeOffset.UtcNow);
            await DesktopLog.WriteAsync("Agent process failed to start.", ex, cancellationToken);
            Publish(Current with
            {
                Agent = ErrorStatus(
                    "Agent failed to start",
                    $"Retrying in {FormatSeconds(delay)} seconds. {ex.Message}")
            });
            return;
        }

        Publish(Current with
        {
            Agent = StartingStatus(
                "Connecting Windows Agent...",
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

        var processId = TryGetProcessId(_agentProcess);
        await StopOwnedAgentAsync(cancellationToken);
        var retryDelay = _agentBackoff.RecordFailure(DateTimeOffset.UtcNow);
        Publish(Current with
        {
            Agent = ErrorStatus(
                "Agent failed to connect",
                $"Retrying in {FormatSeconds(retryDelay)} seconds. See {Path.Combine(_logsDirectory, "agent.log")}",
                processId)
        });
    }

    private async Task EnsureTunnelAsync(CancellationToken cancellationToken)
    {
        await CaptureExitedTunnelAsync();

        var gatewayProbe = await ProbeGatewayAsync(cancellationToken);
        if (gatewayProbe != GatewayProbeState.AgentBridge)
        {
            if (IsAlive(_tunnelProcess))
                await StopOwnedTunnelAsync(cancellationToken);

            Publish(Current with
            {
                Tunnel = StoppedStatus("Tunnel waiting for Gateway")
            });
            return;
        }

        if (IsAlive(_tunnelProcess))
        {
            _tunnelBackoff.ObserveHealthy(DateTimeOffset.UtcNow);
            Publish(Current with
            {
                Tunnel = RunningStatus(
                    "Running",
                    $"Tunnel '{_tunnelName}' forwarding to {_gatewayUrl}",
                    _tunnelProcess!.Id,
                    managed: true)
            });
            return;
        }

        if (!_tunnelBackoff.CanStart(DateTimeOffset.UtcNow, out var remaining))
        {
            Publish(Current with
            {
                Tunnel = ErrorStatus(
                    "Tunnel restart cooling down",
                    $"Automatic retry in {FormatSeconds(remaining)} seconds.")
            });
            return;
        }

        var target = _binaryLocator.ResolveCloudflared(_tunnelName);
        if (target is null)
        {
            Publish(Current with
            {
                Tunnel = ErrorStatus(
                    "cloudflared missing",
                    "Install cloudflared, add it to PATH, or set AGENTBRIDGE_CLOUDFLARED_PATH.")
            });
            return;
        }

        try
        {
            _tunnelProcess = StartProcess(
                target,
                "cloudflared.log",
                _tunnelLogGate,
                BuildTunnelEnvironment());
            _tunnelBackoff.RecordStarted(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            var delay = _tunnelBackoff.RecordFailure(DateTimeOffset.UtcNow);
            await DesktopLog.WriteAsync("Cloudflare Tunnel process failed to start.", ex, cancellationToken);
            Publish(Current with
            {
                Tunnel = ErrorStatus(
                    "Tunnel failed to start",
                    $"Retrying in {FormatSeconds(delay)} seconds. {ex.Message}")
            });
            return;
        }

        Publish(Current with
        {
            Tunnel = StartingStatus(
                $"Starting Tunnel '{_tunnelName}'...",
                _tunnelProcess.Id,
                target.DisplayPath)
        });

        if (await WaitUntilAsync(
                _ => Task.FromResult(IsAlive(_tunnelProcess)),
                TimeSpan.FromSeconds(5),
                cancellationToken))
        {
            Publish(Current with
            {
                Tunnel = RunningStatus(
                    "Running",
                    $"Tunnel '{_tunnelName}' forwarding to {_gatewayUrl}",
                    _tunnelProcess.Id,
                    managed: true)
            });
            return;
        }

        var processId = TryGetProcessId(_tunnelProcess);
        await StopOwnedTunnelAsync(cancellationToken);
        var retryDelay = _tunnelBackoff.RecordFailure(DateTimeOffset.UtcNow);
        Publish(Current with
        {
            Tunnel = ErrorStatus(
                "Tunnel exited during startup",
                $"Retrying in {FormatSeconds(retryDelay)} seconds. See {Path.Combine(_logsDirectory, "cloudflared.log")}",
                processId)
        });
    }

    private IReadOnlyDictionary<string, string> BuildGatewayEnvironment() =>
        new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = _gatewayUrl,
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["AGENTBRIDGE_MANAGED_RUNTIME"] = "1",
            ["AgentSecurity__AuthenticationEnabled"] = "true",
            ["AgentSecurity__TokenEnvironmentVariable"] = InternalTokenStore.TokenEnvironmentVariable,
            [InternalTokenStore.TokenEnvironmentVariable] = _internalToken
        };

    private static IReadOnlyDictionary<string, string> BuildTunnelEnvironment() =>
        new Dictionary<string, string>
        {
            ["AGENTBRIDGE_MANAGED_RUNTIME"] = "1"
        };

    private IReadOnlyDictionary<string, string> BuildAgentEnvironment() =>
        new Dictionary<string, string>
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["AGENTBRIDGE_MANAGED_RUNTIME"] = "1",
            ["Agent__DeviceId"] = _deviceId,
            ["Agent__DisplayName"] = Environment.MachineName,
            ["Agent__GatewayUrl"] = _gatewayUrl,
            ["AgentSecurity__AuthenticationEnabled"] = "true",
            ["AgentSecurity__TokenEnvironmentVariable"] = InternalTokenStore.TokenEnvironmentVariable,
            [InternalTokenStore.TokenEnvironmentVariable] = _internalToken
        };

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
            RedirectStandardInput = true,
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

        if (!_childProcessJob.TryAssign(process, out var assignmentError))
        {
            _ = AppendServiceLogAsync(
                logPath,
                "WARN",
                $"Could not attach PID {process.Id} to AgentBridge child process job: {assignmentError}",
                logGate);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = AppendServiceLogAsync(
            logPath,
            "START",
            $"PID {process.Id}: {target.DisplayPath}",
            logGate);
        return process;
    }

    private async Task<GatewayProbeState> ProbeGatewayAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                $"{_gatewayUrl}/healthz",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return GatewayProbeState.ForeignService;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            var validStatus = root.TryGetProperty("status", out var status)
                && string.Equals(status.GetString(), "ok", StringComparison.Ordinal);
            var validService = root.TryGetProperty("service", out var service)
                && string.Equals(service.GetString(), "AgentBridge.Gateway", StringComparison.Ordinal);

            return validStatus && validService
                ? GatewayProbeState.AgentBridge
                : GatewayProbeState.ForeignService;
        }
        catch (HttpRequestException)
        {
            return GatewayProbeState.Unreachable;
        }
        catch (TaskCanceledException)
        {
            return GatewayProbeState.Unreachable;
        }
        catch (JsonException)
        {
            return GatewayProbeState.ForeignService;
        }
    }

    private async Task<bool> ProbeAgentAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_deviceId))
            return false;

        try
        {
            using var response = await _httpClient.GetAsync(
                $"{_gatewayUrl}/healthz/agent/{Uri.EscapeDataString(_deviceId)}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;

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

    private bool IsGatewayPortInUse()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == _gatewayPort);
        }
        catch (NetworkInformationException)
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

    private async Task CaptureExitedGatewayAsync()
    {
        if (_gatewayProcess is null || IsAlive(_gatewayProcess))
            return;

        var exitCode = SafeExitCode(_gatewayProcess);
        _gatewayProcess.Dispose();
        _gatewayProcess = null;
        var delay = _gatewayBackoff.RecordFailure(DateTimeOffset.UtcNow);
        await AppendServiceLogAsync(
            Path.Combine(_logsDirectory, "gateway.log"),
            "BACKOFF",
            $"Unexpected exit code {exitCode}. Next retry in {FormatSeconds(delay)} seconds.",
            _gatewayLogGate);
    }

    private async Task CaptureExitedAgentAsync()
    {
        if (_agentProcess is null || IsAlive(_agentProcess))
            return;

        var exitCode = SafeExitCode(_agentProcess);
        _agentProcess.Dispose();
        _agentProcess = null;
        var delay = _agentBackoff.RecordFailure(DateTimeOffset.UtcNow);
        await AppendServiceLogAsync(
            Path.Combine(_logsDirectory, "agent.log"),
            "BACKOFF",
            $"Unexpected exit code {exitCode}. Next retry in {FormatSeconds(delay)} seconds.",
            _agentLogGate);
    }

    private async Task CaptureExitedTunnelAsync()
    {
        if (_tunnelProcess is null || IsAlive(_tunnelProcess))
            return;

        var exitCode = SafeExitCode(_tunnelProcess);
        _tunnelProcess.Dispose();
        _tunnelProcess = null;
        var delay = _tunnelBackoff.RecordFailure(DateTimeOffset.UtcNow);
        await AppendServiceLogAsync(
            Path.Combine(_logsDirectory, "cloudflared.log"),
            "BACKOFF",
            $"Unexpected exit code {exitCode}. Next retry in {FormatSeconds(delay)} seconds.",
            _tunnelLogGate);
    }

    private async Task StopOwnedTunnelAsync(CancellationToken cancellationToken)
    {
        await StopProcessAsync(_tunnelProcess, "cloudflared.log", _tunnelLogGate, cancellationToken);
        _tunnelProcess = null;
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
            $"Requesting graceful stop for PID {process!.Id}.",
            logGate);

        var exitedGracefully = false;
        try
        {
            await process.StandardInput.WriteLineAsync("stop");
            await process.StandardInput.FlushAsync();

            using var gracefulTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            gracefulTimeout.CancelAfter(GracefulStopTimeout);
            await process.WaitForExitAsync(gracefulTimeout.Token);
            exitedGracefully = true;
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        if (!exitedGracefully && IsAlive(process))
        {
            await AppendServiceLogAsync(
                logPath,
                "WARN",
                $"Graceful stop exceeded {GracefulStopTimeout.TotalSeconds:0} seconds. Killing process tree.",
                logGate);

            try
            {
                process.Kill(entireProcessTree: true);
                using var forcedTimeout = new CancellationTokenSource(ForcedStopTimeout);
                await process.WaitForExitAsync(forcedTimeout.Token);
            }
            catch (InvalidOperationException)
            {
            }
            catch (OperationCanceledException)
            {
                await AppendServiceLogAsync(
                    logPath,
                    "WARN",
                    "Process tree did not exit before the forced-stop timeout.",
                    logGate);
            }
        }

        process.Dispose();
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

    private static int FormatSeconds(TimeSpan value) =>
        Math.Max(1, (int)Math.Ceiling(value.TotalSeconds));

    private static bool TryNormalizeGatewayUrl(
        string value,
        out string normalizedUrl,
        out int port)
    {
        normalizedUrl = string.Empty;
        port = 0;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)
            || !IPAddress.IsLoopback(address))
        {
            return false;
        }

        normalizedUrl = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        port = uri.Port;
        return true;
    }

    private static string NormalizeTunnelName(string value)
    {
        var normalized = value.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? DefaultTunnelName
            : normalized;
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

    private enum GatewayProbeState
    {
        Unreachable,
        AgentBridge,
        ForeignService
    }
}
