using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace LocalMcp.Agent.AndroidAdb;

public interface IAdbExecutor
{
    Task<AdbExecutionResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        int maxOutputBytes = 1024 * 1024,
        CancellationToken cancellationToken = default);
}

public sealed record AdbExecutionResult(
    int ExitCode,
    byte[] StandardOutput,
    string StandardError,
    bool OutputLimitExceeded)
{
    public string StandardOutputText => System.Text.Encoding.UTF8.GetString(StandardOutput).Trim();
}

public sealed class AdbProcessExecutor : IAdbExecutor
{
    private readonly AndroidAdbOptions _options;

    public AdbProcessExecutor(IOptions<AndroidAdbOptions> options)
    {
        _options = options.Value;
    }

    public async Task<AdbExecutionResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        int maxOutputBytes = 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (maxOutputBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.AdbPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(_options.Serial);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new AdbUnavailableException("ADB could not be started.");
        }
        catch (Win32Exception ex)
        {
            throw new AdbUnavailableException($"ADB executable '{_options.AdbPath}' was not found or could not be started.", ex);
        }

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(_options.CommandTimeoutSeconds));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedSource.Token);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, maxOutputBytes, linkedSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
            var (output, exceeded) = await stdoutTask;
            var error = await stderrTask;
            return new AdbExecutionResult(process.ExitCode, output, error.Trim(), exceeded);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"ADB command exceeded {_options.CommandTimeoutSeconds} seconds.");
            throw;
        }
    }

    private static async Task<(byte[] Output, bool Exceeded)> ReadBoundedAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        var exceeded = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            var remaining = maxBytes - (int)output.Length;
            if (remaining > 0)
                output.Write(buffer, 0, Math.Min(read, remaining));
            if (read > remaining)
                exceeded = true;
        }

        return (output.ToArray(), exceeded);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed class AdbUnavailableException : Exception
{
    public AdbUnavailableException(string message) : base(message) { }
    public AdbUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
