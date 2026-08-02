using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LocalMcp.BuildingBlocks.Configuration;

namespace AgentBridge.Desktop.Services;

public static class DesktopLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool IsEnabled { get; set; } = false;

    public static async Task WriteAsync(
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return;
        try
        {
            var directory = LocalConfigurationPaths.GetLogsDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "desktop.log");
            var builder = new StringBuilder()
                .Append(DateTimeOffset.UtcNow.ToString("O"))
                .Append("  ")
                .AppendLine(message);

            if (exception is not null)
                builder.AppendLine(exception.ToString());

            await Gate.WaitAsync(cancellationToken);
            try
            {
                await File.AppendAllTextAsync(path, builder.ToString(), cancellationToken);
            }
            finally
            {
                Gate.Release();
            }
        }
        catch
        {
            // Crash logging must never create a second failure.
        }
    }
}
