using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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
        bool visible,
        bool elevated,
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

        if (elevated && !visible)
        {
            return PowerShellFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                "elevated=true is only supported for visible PowerShell execution.");
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

        if (visible)
        {
            return await PowerShellExecuteVisibleAsync(
                executable,
                workingDirectory,
                script,
                elevated,
                timeoutSeconds,
                maxOutputBytes,
                commandId,
                cancellationToken);
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

    private async Task<CommandResult<PowerShellExecuteResult>> PowerShellExecuteVisibleAsync(
        string executable,
        string workingDirectory,
        string script,
        bool elevated,
        int timeoutSeconds,
        int maxOutputBytes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var executionDirectory = Path.Combine(
            Path.GetTempPath(),
            "LocalMcp",
            "powershell-visible",
            commandId.ToString("N"));
        var userScriptPath = Path.Combine(executionDirectory, "user-script.ps1");
        var runnerScriptPath = Path.Combine(executionDirectory, "runner.ps1");
        var wrapperScriptPath = Path.Combine(executionDirectory, "visible-wrapper.ps1");
        var commandFilePath = Path.Combine(executionDirectory, "launch.cmd");
        var outputPath = Path.Combine(executionDirectory, "output.log");
        var statusPath = Path.Combine(executionDirectory, "status.json");
        var cancelPath = Path.Combine(executionDirectory, "cancel.signal");
        var stopwatch = Stopwatch.StartNew();
        var canCleanup = true;

        try
        {
            Directory.CreateDirectory(executionDirectory);
            await File.WriteAllTextAsync(
                userScriptPath,
                script,
                PowerShellOutputEncoding,
                cancellationToken);
            await File.WriteAllTextAsync(
                runnerScriptPath,
                BuildVisiblePowerShellRunnerScript(userScriptPath, outputPath),
                PowerShellOutputEncoding,
                cancellationToken);
            await File.WriteAllTextAsync(
                wrapperScriptPath,
                BuildVisiblePowerShellWrapperScript(
                    executable,
                    workingDirectory,
                    runnerScriptPath,
                    outputPath,
                    statusPath,
                    cancelPath,
                    timeoutSeconds),
                PowerShellOutputEncoding,
                cancellationToken);
            await File.WriteAllTextAsync(
                commandFilePath,
                BuildVisiblePowerShellCommandFile(executable, wrapperScriptPath),
                PowerShellOutputEncoding,
                cancellationToken);

            var startInfo = CreateVisiblePowerShellStartInfo(
                commandFilePath,
                workingDirectory,
                elevated);
            using var process = new Process { StartInfo = startInfo };

            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
            {
                return PowerShellFailure(
                    commandId,
                    ErrorCodes.InternalError,
                    "The visible PowerShell console could not be started.");
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TrySignalVisiblePowerShellCancellation(cancelPath);
                await WaitForVisiblePowerShellExitAsync(process);
                canCleanup = process.HasExited;

                return PowerShellFailure(
                    commandId,
                    ErrorCodes.CommandCancelled,
                    "The visible PowerShell command was cancelled.");
            }

            stopwatch.Stop();
            var output = await ReadPowerShellOutputFileAsync(outputPath, maxOutputBytes);
            var bounded = BoundPowerShellOutput(
                PowerShellOutputEncoding.GetString(output.Bytes),
                string.Empty,
                maxOutputBytes);
            var status = ReadVisiblePowerShellStatus(
                statusPath,
                process.HasExited ? process.ExitCode : null,
                stopwatch.ElapsedMilliseconds);

            return new CommandResult<PowerShellExecuteResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new PowerShellExecuteResult
                {
                    WorkingDirectory = workingDirectory,
                    Success = !status.TimedOut &&
                        !status.Cancelled &&
                        status.ExitCode == 0,
                    ExitCode = status.ExitCode,
                    Stdout = bounded.Stdout,
                    Stderr = bounded.Stderr,
                    DurationMs = status.DurationMs,
                    TimedOut = status.TimedOut,
                    Truncated = output.Truncated || bounded.Truncated,
                    BytesReturned = bounded.BytesReturned
                }
            };
        }
        catch (OperationCanceledException)
        {
            TrySignalVisiblePowerShellCancellation(cancelPath);
            return PowerShellFailure(
                commandId,
                ErrorCodes.CommandCancelled,
                "The visible PowerShell command was cancelled.");
        }
        catch (Win32Exception ex) when (elevated && ex.NativeErrorCode == 1223)
        {
            return PowerShellFailure(
                commandId,
                ErrorCodes.CommandCancelled,
                "UAC elevation was declined by the user.");
        }
        catch (Exception ex) when (
            ex is Win32Exception or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Visible PowerShell execution failed to start or communicate.");
            return PowerShellFailure(
                commandId,
                ErrorCodes.InternalError,
                "Visible PowerShell execution failed to start or communicate.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected visible PowerShell execution failure for command {CommandId}.",
                commandId);
            return PowerShellFailure(
                commandId,
                ErrorCodes.InternalError,
                "An unexpected error occurred while executing visible PowerShell.");
        }
        finally
        {
            if (canCleanup)
                TryDeleteVisiblePowerShellDirectory(executionDirectory);
        }
    }

    internal static ProcessStartInfo CreateVisiblePowerShellStartInfo(
        string commandFilePath,
        string workingDirectory,
        bool elevated)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(commandFilePath);
        if (elevated)
            startInfo.Verb = "runas";

        return startInfo;
    }

    private static string BuildVisiblePowerShellRunnerScript(
        string userScriptPath,
        string outputPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("$ErrorActionPreference = 'Continue'");
        builder.AppendLine("[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)");
        builder.AppendLine("$OutputEncoding = [Console]::OutputEncoding");
        builder.AppendLine("try {");
        builder.Append("    & ");
        builder.Append(PowerShellSingleQuote(userScriptPath));
        builder.Append(" *>&1 | Tee-Object -LiteralPath ");
        builder.AppendLine(PowerShellSingleQuote(outputPath));
        builder.AppendLine("    if ($LASTEXITCODE -is [int]) { exit [int]$LASTEXITCODE }");
        builder.AppendLine("    if ($?) { exit 0 }");
        builder.AppendLine("    exit 1");
        builder.AppendLine("}");
        builder.AppendLine("catch {");
        builder.AppendLine("    $__localMcpError = $_ | Out-String");
        builder.Append("    $__localMcpError | Tee-Object -LiteralPath ");
        builder.Append(PowerShellSingleQuote(outputPath));
        builder.AppendLine(" -Append | Write-Error");
        builder.AppendLine("    exit 1");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string BuildVisiblePowerShellWrapperScript(
        string executable,
        string workingDirectory,
        string runnerScriptPath,
        string outputPath,
        string statusPath,
        string cancelPath,
        int timeoutSeconds)
    {
        var builder = new StringBuilder();
        builder.AppendLine("$ErrorActionPreference = 'Stop'");
        builder.AppendLine("[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)");
        builder.AppendLine("$OutputEncoding = [Console]::OutputEncoding");
        builder.AppendLine("$__localMcpSecretFragments = @('TOKEN','SECRET','PASSWORD','PASSWD','API_KEY','APIKEY','PRIVATE_KEY','CLIENT_SECRET','CREDENTIAL','COOKIE','BEARER')");
        builder.AppendLine("Get-ChildItem Env: | ForEach-Object {");
        builder.AppendLine("    $__localMcpNormalized = $_.Name.Replace('-', '_').ToUpperInvariant()");
        builder.AppendLine("    foreach ($__localMcpFragment in $__localMcpSecretFragments) {");
        builder.AppendLine("        if ($__localMcpNormalized.Contains($__localMcpFragment)) {");
        builder.AppendLine("            [Environment]::SetEnvironmentVariable($_.Name, $null, 'Process')");
        builder.AppendLine("            break");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine("$env:POWERSHELL_TELEMETRY_OPTOUT = '1'");
        builder.AppendLine("$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'");
        builder.AppendLine("$env:NO_COLOR = '1'");
        builder.AppendLine("$__localMcpStartInfo = [System.Diagnostics.ProcessStartInfo]::new()");
        builder.Append("$__localMcpStartInfo.FileName = ");
        builder.AppendLine(PowerShellSingleQuote(executable));
        builder.Append("$__localMcpStartInfo.WorkingDirectory = ");
        builder.AppendLine(PowerShellSingleQuote(workingDirectory));
        builder.AppendLine("$__localMcpStartInfo.UseShellExecute = $false");
        builder.AppendLine("$__localMcpStartInfo.CreateNoWindow = $false");
        builder.AppendLine("$__localMcpStartInfo.ArgumentList.Add('-NoLogo')");
        builder.AppendLine("$__localMcpStartInfo.ArgumentList.Add('-NoProfile')");
        builder.AppendLine("$__localMcpStartInfo.ArgumentList.Add('-ExecutionPolicy')");
        builder.AppendLine("$__localMcpStartInfo.ArgumentList.Add('Bypass')");
        builder.AppendLine("$__localMcpStartInfo.ArgumentList.Add('-File')");
        builder.Append("$__localMcpStartInfo.ArgumentList.Add(");
        builder.Append(PowerShellSingleQuote(runnerScriptPath));
        builder.AppendLine(")");
        builder.AppendLine("$__localMcpProcess = [System.Diagnostics.Process]::new()");
        builder.AppendLine("$__localMcpProcess.StartInfo = $__localMcpStartInfo");
        builder.AppendLine("$__localMcpWatch = [System.Diagnostics.Stopwatch]::StartNew()");
        builder.AppendLine("$__localMcpTimedOut = $false");
        builder.AppendLine("$__localMcpCancelled = $false");
        builder.AppendLine("$__localMcpExitCode = $null");
        builder.AppendLine("try {");
        builder.AppendLine("    if (-not $__localMcpProcess.Start()) { throw 'PowerShell child process could not be started.' }");
        builder.AppendLine("    while (-not $__localMcpProcess.WaitForExit(250)) {");
        builder.Append("        if (Test-Path -LiteralPath ");
        builder.Append(PowerShellSingleQuote(cancelPath));
        builder.AppendLine(") {");
        builder.AppendLine("            $__localMcpCancelled = $true");
        builder.AppendLine("            try { $__localMcpProcess.Kill($true) } catch { }");
        builder.AppendLine("            break");
        builder.AppendLine("        }");
        builder.Append("        if ($__localMcpWatch.ElapsedMilliseconds -ge ");
        builder.Append(timeoutSeconds * 1000L);
        builder.AppendLine(") {");
        builder.AppendLine("            $__localMcpTimedOut = $true");
        builder.AppendLine("            try { $__localMcpProcess.Kill($true) } catch { }");
        builder.AppendLine("            break");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine("    if ($__localMcpTimedOut -or $__localMcpCancelled) {");
        builder.AppendLine("        try { $__localMcpProcess.WaitForExit() } catch { }");
        builder.AppendLine("    } else {");
        builder.AppendLine("        $__localMcpExitCode = $__localMcpProcess.ExitCode");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine("catch {");
        builder.AppendLine("    $__localMcpFailure = $_ | Out-String");
        builder.Append("    $__localMcpFailure | Tee-Object -LiteralPath ");
        builder.Append(PowerShellSingleQuote(outputPath));
        builder.AppendLine(" -Append | Write-Error");
        builder.AppendLine("    $__localMcpExitCode = 1");
        builder.AppendLine("}");
        builder.AppendLine("finally {");
        builder.AppendLine("    $__localMcpWatch.Stop()");
        builder.AppendLine("    $__localMcpStatus = [ordered]@{");
        builder.AppendLine("        timedOut = $__localMcpTimedOut");
        builder.AppendLine("        cancelled = $__localMcpCancelled");
        builder.AppendLine("        exitCode = $__localMcpExitCode");
        builder.AppendLine("        durationMs = $__localMcpWatch.ElapsedMilliseconds");
        builder.AppendLine("    }");
        builder.Append("    $__localMcpStatus | ConvertTo-Json -Compress | Set-Content -LiteralPath ");
        builder.Append(PowerShellSingleQuote(statusPath));
        builder.AppendLine(" -Encoding utf8NoBOM");
        builder.AppendLine("    $__localMcpProcess.Dispose()");
        builder.AppendLine("}");
        builder.AppendLine("if ($__localMcpCancelled) { exit 125 }");
        builder.AppendLine("if ($__localMcpTimedOut) { exit 124 }");
        builder.AppendLine("if ($null -eq $__localMcpExitCode) { exit 1 }");
        builder.AppendLine("exit [int]$__localMcpExitCode");
        return builder.ToString();
    }

    private static string BuildVisiblePowerShellCommandFile(
        string executable,
        string wrapperScriptPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("@echo off");
        builder.AppendLine("chcp 65001 >nul");
        builder.AppendLine("title LocalMcp PowerShell");
        builder.Append('"');
        builder.Append(executable);
        builder.Append("\" -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"");
        builder.Append(wrapperScriptPath);
        builder.AppendLine("\"");
        builder.AppendLine("exit /b %ERRORLEVEL%");
        return builder.ToString();
    }

    private static string PowerShellSingleQuote(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static async Task<PowerShellBoundedOutput> ReadPowerShellOutputFileAsync(
        string path,
        int maxBytes)
    {
        if (!File.Exists(path))
            return new PowerShellBoundedOutput([], Truncated: false);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 8192,
            useAsync: true);
        return await ReadPowerShellOutputAsync(stream, maxBytes);
    }

    private static VisiblePowerShellStatus ReadVisiblePowerShellStatus(
        string path,
        int? fallbackExitCode,
        long fallbackDurationMs)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new VisiblePowerShellStatus(
                    TimedOut: fallbackExitCode == 124,
                    Cancelled: fallbackExitCode == 125,
                    ExitCode: fallbackExitCode,
                    DurationMs: fallbackDurationMs);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var timedOut = root.TryGetProperty("timedOut", out var timedOutElement) &&
                timedOutElement.ValueKind == JsonValueKind.True;
            var cancelled = root.TryGetProperty("cancelled", out var cancelledElement) &&
                cancelledElement.ValueKind == JsonValueKind.True;
            int? exitCode = null;
            if (root.TryGetProperty("exitCode", out var exitCodeElement) &&
                exitCodeElement.ValueKind == JsonValueKind.Number &&
                exitCodeElement.TryGetInt32(out var parsedExitCode))
            {
                exitCode = parsedExitCode;
            }

            var durationMs = fallbackDurationMs;
            if (root.TryGetProperty("durationMs", out var durationElement) &&
                durationElement.ValueKind == JsonValueKind.Number &&
                durationElement.TryGetInt64(out var parsedDuration))
            {
                durationMs = parsedDuration;
            }

            return new VisiblePowerShellStatus(
                timedOut,
                cancelled,
                exitCode,
                durationMs);
        }
        catch (JsonException)
        {
            return new VisiblePowerShellStatus(
                TimedOut: fallbackExitCode == 124,
                Cancelled: fallbackExitCode == 125,
                ExitCode: fallbackExitCode,
                DurationMs: fallbackDurationMs);
        }
    }

    private static void TrySignalVisiblePowerShellCancellation(string cancelPath)
    {
        try
        {
            File.WriteAllText(cancelPath, string.Empty, PowerShellOutputEncoding);
        }
        catch
        {
        }
    }

    private static async Task WaitForVisiblePowerShellExitAsync(Process process)
    {
        try
        {
            using var waitSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(waitSource.Token);
        }
        catch
        {
            TryKillPowerShellProcess(process);
        }
    }

    private static void TryDeleteVisiblePowerShellDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record VisiblePowerShellStatus(
        bool TimedOut,
        bool Cancelled,
        int? ExitCode,
        long DurationMs);

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
