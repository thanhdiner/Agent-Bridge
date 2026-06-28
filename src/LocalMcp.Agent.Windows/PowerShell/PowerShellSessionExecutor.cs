using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LocalMcp.Agent.Windows.PowerShell;

/// <summary>
/// Runs a PowerShell script asynchronously for a given <see cref="PowerShellSessionState"/>.
/// The method returns immediately after the process starts; output is streamed
/// into the session's bounded buffer in background tasks.
/// </summary>
internal class PowerShellSessionExecutor
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    private readonly PowerShellSessionRegistry _registry;
    private readonly ILogger<PowerShellSessionExecutor> _logger;

    public PowerShellSessionExecutor(
        PowerShellSessionRegistry registry,
        ILogger<PowerShellSessionExecutor> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Starts the PowerShell process for <paramref name="session"/> and fires off
    /// background tasks for stdout/stderr draining and process monitoring.
    /// Returns immediately; callers should not await the background work.
    /// </summary>
    public virtual void StartBackground(
        PowerShellSessionState session,
        string executable,
        string workingDirectory,
        string script,
        int timeoutSeconds)
    {
        _ = RunAsync(session, executable, workingDirectory, script, timeoutSeconds);
    }

    private async Task RunAsync(
        PowerShellSessionState session,
        string executable,
        string workingDirectory,
        string script,
        int timeoutSeconds)
    {
        var startInfo = BuildStartInfo(executable, workingDirectory);
        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                session.TryTransition(PowerShellSessionStateValue.Failed);
                _registry.OnSessionTerminated(session);
                _logger.LogWarning("Session {SessionId}: pwsh.exe failed to start.", session.SessionId);
                return;
            }

            session.Process = process;
            try
            {
                // Drain stdout and stderr concurrently
                var stdoutTask = DrainStreamAsync(
                    process.StandardOutput.BaseStream,
                    session.AppendStdout,
                    session.Cts.Token);
                var stderrTask = DrainStreamAsync(
                    process.StandardError.BaseStream,
                    session.AppendStderr,
                    session.Cts.Token);

                // Feed script via stdin then close it
                try
                {
                    await process.StandardInput.WriteAsync(
                        script.AsMemory(),
                        session.Cts.Token);
                    await process.StandardInput.FlushAsync();
                }
                catch (IOException) { /* pwsh may exit before consuming all stdin */ }
                catch (OperationCanceledException) { /* handled below */ }
                finally
                {
                    try { process.StandardInput.Close(); }
                    catch { /* ignore */ }
                }

                // Wait for process exit with timeout
                using var timeoutSource = new CancellationTokenSource(
                    TimeSpan.FromSeconds(timeoutSeconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    session.Cts.Token,
                    timeoutSource.Token);

                bool timedOut;
                try
                {
                    await process.WaitForExitAsync(linked.Token);
                    timedOut = false;
                }
                catch (OperationCanceledException) when (
                    timeoutSource.IsCancellationRequested &&
                    !session.Cts.IsCancellationRequested)
                {
                    timedOut = true;
                    TryKill(process);
                    await WaitSafeAsync(process);
                }
                catch (OperationCanceledException)
                {
                    // External cancel via Cts
                    TryKill(process);
                    await WaitSafeAsync(process);
                    timedOut = false;
                }

                // Wait for drain tasks to complete
                await Task.WhenAll(stdoutTask, stderrTask);

                // Atomic terminal state transition (only one wins)
                int? exitCode = null;
                try { exitCode = process.HasExited ? process.ExitCode : null; }
                catch { /* ignore */ }

                if (timedOut)
                {
                    session.TryTransition(PowerShellSessionStateValue.TimedOut);
                }
                else if (session.Cts.IsCancellationRequested)
                {
                    session.TryTransition(PowerShellSessionStateValue.Cancelled);
                }
                else if (exitCode == 0)
                {
                    session.TryTransition(PowerShellSessionStateValue.Completed, exitCode);
                }
                else
                {
                    session.TryTransition(PowerShellSessionStateValue.Failed, exitCode);
                }

                _registry.OnSessionTerminated(session);
                _logger.LogDebug(
                    "Session {SessionId} finished: state={State}, exitCode={ExitCode}",
                    session.SessionId, session.State, exitCode);
            }
            finally
            {
                session.Process = null;
            }
        }
        catch (Exception ex) when (
            ex is Win32Exception or
            InvalidOperationException or
            IOException)
        {
            _logger.LogWarning(ex, "Session {SessionId}: process communication error.", session.SessionId);
            TryKill(process);
            session.TryTransition(PowerShellSessionStateValue.Failed);
            _registry.OnSessionTerminated(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId}: unexpected error.", session.SessionId);
            TryKill(process);
            session.TryTransition(PowerShellSessionStateValue.Failed);
            _registry.OnSessionTerminated(session);
        }
    }

    private static async Task DrainStreamAsync(
        Stream stream,
        Action<byte[], int> append,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                append(buffer, read);
            }
        }
        catch (OperationCanceledException) { /* session cancelled/timed out */ }
        catch (IOException) { /* pipe closed */ }
        catch (Exception ex)
        {
            // Don't let drain task kill the main flow
            _ = ex;
        }
    }

    private static ProcessStartInfo BuildStartInfo(string executable, string workingDirectory)
    {
        var si = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };

        si.ArgumentList.Add("-NoLogo");
        si.ArgumentList.Add("-NoProfile");
        si.ArgumentList.Add("-NonInteractive");
        si.ArgumentList.Add("-Command");
        si.ArgumentList.Add("-");

        // Strip secret-like env vars (same list as FileSystemExecutor)
        foreach (var key in si.Environment.Keys.ToArray())
        {
            if (FileSystem.FileSystemExecutor.IsSensitiveEnvironmentVariable(key))
                si.Environment.Remove(key);
        }

        si.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        si.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        si.Environment["GIT_TERMINAL_PROMPT"] = "0";
        si.Environment["GCM_INTERACTIVE"] = "Never";
        si.Environment["NO_COLOR"] = "1";
        si.Environment["TERM"] = "dumb";
        si.Environment["__COMPAT_LAYER"] = "RunAsInvoker";

        return si;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* ignore */ }
    }

    private static async Task WaitSafeAsync(Process process)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cts.Token);
        }
        catch
        {
            TryKill(process);
        }
    }
}
