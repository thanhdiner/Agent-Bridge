using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.FileSystem;

public sealed partial class FileSystemExecutor
{
    private const int MaxPowerShellScriptCharacters = 65_536;

    private static readonly Encoding PowerShellOutputEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    public async Task<CommandResult<PowerShellExecuteResult>> PowerShellExecuteAsync(
        string workingDirectory,
        string script,
        int timeoutSeconds,
        int maxOutputBytes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(script) ||
            script.Length > MaxPowerShellScriptCharacters ||
            script.Contains('\0'))
        {
            return PowerShellFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                "script must be non-empty, contain no NUL characters, and be at most 65536 characters.");
        }

        if (timeoutSeconds is < 1 or > 900)
        {
            return PowerShellFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                "timeoutSeconds must be between 1 and 900.");
        }

        if (maxOutputBytes is < 1024 or > 4_194_304)
        {
            return PowerShellFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                "maxOutputBytes must be between 1024 and 4194304.");
        }

        if (IsCurrentProcessElevated())
        {
            return PowerShellFailure(
                commandId,
                ErrorCodes.AccessDenied,
                "PowerShell execution is disabled while the Windows agent is running elevated.");
        }

        var executable = ResolveToolExecutable("pwsh.exe", workingDirectory);
        if (executable is null)
        {
            return PowerShellFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                "PowerShell 7 (pwsh.exe) is not available on the Windows agent.");
        }

        var startInfo = CreatePowerShellStartInfo(executable, workingDirectory);
        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!process.Start())
            {
                return PowerShellFailure(
                    commandId,
                    ErrorCodes.InternalError,
                    "PowerShell 7 could not be started.");
            }

            var stdoutTask = ReadPowerShellOutputAsync(
                process.StandardOutput.BaseStream,
                maxOutputBytes);
            var stderrTask = ReadPowerShellOutputAsync(
                process.StandardError.BaseStream,
                maxOutputBytes);

            var timedOut = false;
            using var timeoutSource = new CancellationTokenSource(
                TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

            try
            {
                try
                {
                    await process.StandardInput.WriteAsync(
                        script.AsMemory(),
                        linkedSource.Token);
                    await process.StandardInput.FlushAsync();
                }
                catch (IOException)
                {
                    // pwsh may exit before consuming all stdin.
                }
                finally
                {
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch
                    {
                    }
                }

                await process.WaitForExitAsync(linkedSource.Token);
            }
            catch (OperationCanceledException) when (
                timeoutSource.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                TryKillPowerShellProcess(process);
                await WaitForPowerShellExitAsync(process);
            }
            catch (OperationCanceledException)
            {
                TryKillPowerShellProcess(process);
                await WaitForPowerShellExitAsync(process);

                return PowerShellFailure(
                    commandId,
                    ErrorCodes.CommandCancelled,
                    "The PowerShell command was cancelled.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            stopwatch.Stop();

            var bounded = BoundPowerShellOutput(
                PowerShellOutputEncoding.GetString(stdout.Bytes),
                PowerShellOutputEncoding.GetString(stderr.Bytes),
                maxOutputBytes);
            int? exitCode = process.HasExited ? process.ExitCode : null;

            return new CommandResult<PowerShellExecuteResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new PowerShellExecuteResult
                {
                    WorkingDirectory = workingDirectory,
                    Success = !timedOut && exitCode == 0,
                    ExitCode = exitCode,
                    Stdout = bounded.Stdout,
                    Stderr = bounded.Stderr,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    TimedOut = timedOut,
                    Truncated = stdout.Truncated ||
                        stderr.Truncated ||
                        bounded.Truncated,
                    BytesReturned = bounded.BytesReturned
                }
            };
        }
        catch (OperationCanceledException)
        {
            TryKillPowerShellProcess(process);
            return PowerShellFailure(
                commandId,
                ErrorCodes.CommandCancelled,
                "The PowerShell command was cancelled.");
        }
        catch (Exception ex) when (
            ex is Win32Exception or
            InvalidOperationException or
            IOException)
        {
            TryKillPowerShellProcess(process);
            _logger.LogWarning(ex, "PowerShell execution failed to start or communicate.");
            return PowerShellFailure(
                commandId,
                ErrorCodes.InternalError,
                "PowerShell execution failed to start or communicate.");
        }
        catch (Exception ex)
        {
            TryKillPowerShellProcess(process);
            _logger.LogError(
                ex,
                "Unexpected PowerShell execution failure for command {CommandId}.",
                commandId);
            return PowerShellFailure(
                commandId,
                ErrorCodes.InternalError,
                "An unexpected error occurred while executing PowerShell.");
        }
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(
        string executable,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = PowerShellOutputEncoding,
            StandardOutputEncoding = PowerShellOutputEncoding,
            StandardErrorEncoding = PowerShellOutputEncoding
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("-");

        foreach (var variableName in startInfo.Environment.Keys.ToArray())
        {
            if (IsSensitiveEnvironmentVariable(variableName))
                startInfo.Environment.Remove(variableName);
        }

        startInfo.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["TERM"] = "dumb";
        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";

        return startInfo;
    }

    internal static bool IsSensitiveEnvironmentVariable(string name)
    {
        var normalized = name
            .Replace("-", "_", StringComparison.Ordinal)
            .ToUpperInvariant();

        string[] sensitiveFragments =
        [
            "TOKEN",
            "SECRET",
            "PASSWORD",
            "PASSWD",
            "API_KEY",
            "APIKEY",
            "PRIVATE_KEY",
            "CLIENT_SECRET",
            "CREDENTIAL",
            "COOKIE",
            "BEARER"
        ];

        return sensitiveFragments.Any(fragment =>
            normalized.Contains(fragment, StringComparison.Ordinal));
    }

    internal static PowerShellOutputAllocation BoundPowerShellOutput(
        string stdout,
        string stderr,
        int maxBytes)
    {
        var stdoutBytes = PowerShellOutputEncoding.GetByteCount(stdout);
        var stderrBytes = PowerShellOutputEncoding.GetByteCount(stderr);
        if (stdoutBytes + stderrBytes <= maxBytes)
        {
            return new PowerShellOutputAllocation(
                stdout,
                stderr,
                stdoutBytes + stderrBytes,
                Truncated: false);
        }

        var stdoutBudget = maxBytes / 2;
        var stderrBudget = maxBytes - stdoutBudget;

        if (stdoutBytes < stdoutBudget)
        {
            stderrBudget += stdoutBudget - stdoutBytes;
            stdoutBudget = stdoutBytes;
        }
        else if (stderrBytes < stderrBudget)
        {
            stdoutBudget += stderrBudget - stderrBytes;
            stderrBudget = stderrBytes;
        }

        var boundedStdout = TruncatePowerShellUtf8(stdout, stdoutBudget);
        var boundedStderr = TruncatePowerShellUtf8(stderr, stderrBudget);
        return new PowerShellOutputAllocation(
            boundedStdout.Text,
            boundedStderr.Text,
            boundedStdout.Bytes + boundedStderr.Bytes,
            Truncated: true);
    }

    private static (string Text, int Bytes) TruncatePowerShellUtf8(
        string value,
        int maxBytes)
    {
        if (maxBytes <= 0 || value.Length == 0)
            return (string.Empty, 0);

        var builder = new StringBuilder(Math.Min(value.Length, maxBytes));
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
                break;

            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }

        return (builder.ToString(), bytes);
    }

    private static async Task<PowerShellBoundedOutput> ReadPowerShellOutputAsync(
        Stream stream,
        int maxBytes)
    {
        using var destination = new MemoryStream(Math.Min(maxBytes, 65_536));
        var buffer = new byte[8192];
        var truncated = false;
        int read;

        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            var remaining = maxBytes - (int)destination.Length;
            if (remaining > 0)
                destination.Write(buffer, 0, Math.Min(remaining, read));
            if (read > remaining)
                truncated = true;
        }

        return new PowerShellBoundedOutput(
            destination.ToArray(),
            truncated);
    }

    private static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return true;
        }
    }

    private static void TryKillPowerShellProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static async Task WaitForPowerShellExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch
        {
        }
    }

    private static CommandResult<PowerShellExecuteResult> PowerShellFailure(
        Guid commandId,
        string code,
        string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };

    private sealed record PowerShellBoundedOutput(
        byte[] Bytes,
        bool Truncated);

    internal sealed record PowerShellOutputAllocation(
        string Stdout,
        string Stderr,
        int BytesReturned,
        bool Truncated);
}
