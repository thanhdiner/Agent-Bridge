using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalMcp.Gateway.Mcp;

/// <summary>
/// A small, persistent browser-level CDP connection for targets that the
/// upstream chrome-devtools MCP page picker does not expose (extension side
/// panels, service workers, and out-of-process iframes).
/// </summary>
public sealed class ChromeCdpClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions WireJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly ILogger<ChromeCdpClient> _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private ClientWebSocket? _socket;
    private Task? _receiveTask;
    private long _nextId;

    public ChromeCdpClient(ILogger<ChromeCdpClient> logger) => _logger = logger;

    public async Task<JsonElement> SendAsync(
        string method,
        object? parameters = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("CDP request ID collision.");

        try
        {
            var message = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id,
                method,
                @params = parameters ?? new { },
                sessionId
            }, WireJson);
            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                await _socket!.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendGate.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            return await completion.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_socket?.State == WebSocketState.Open) return;
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_socket?.State == WebSocketState.Open) return;
            if (_receiveTask is not null) await _receiveTask;
            _socket?.Dispose();

            var userDataDir = Environment.GetEnvironmentVariable("AGENTBRIDGE_CHROME_USER_DATA_DIR");
            if (string.IsNullOrWhiteSpace(userDataDir))
            {
                userDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Google", "Chrome", "User Data");
            }
            var activePortPath = Path.Combine(userDataDir, "DevToolsActivePort");
            if (!File.Exists(activePortPath))
                throw new InvalidOperationException($"Chrome DevToolsActivePort was not found at {activePortPath}. Enable remote debugging in Chrome.");

            var lines = (await File.ReadAllLinesAsync(activePortPath, cancellationToken))
                .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            if (lines.Length < 2 || !int.TryParse(lines[0], out var port) || port is < 1 or > 65535 ||
                !lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
                throw new InvalidOperationException("Chrome DevToolsActivePort has an invalid browser endpoint.");

            var socket = new ClientWebSocket();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Chrome may show its remote debugging permission prompt here.
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{lines[1]}"), timeout.Token);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            _socket = socket;
            _receiveTask = ReceiveLoopAsync(socket);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket)
    {
        Exception closed = new IOException("Chrome DevTools connection closed.");
        try
        {
            var buffer = new byte[64 * 1024];
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (message.Length + result.Count > 16 * 1024 * 1024)
                        throw new InvalidDataException("Chrome DevTools message exceeded 16 MB.");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idValue) || !idValue.TryGetInt64(out var id) ||
                    !_pending.TryRemove(id, out var completion)) continue;
                if (root.TryGetProperty("error", out var error))
                    completion.TrySetException(new InvalidOperationException(error.ToString()));
                else if (root.TryGetProperty("result", out var value))
                    completion.TrySetResult(value.Clone());
                else
                    completion.TrySetException(new InvalidDataException("CDP response has no result."));
            }
        }
        catch (Exception ex)
        {
            closed = ex;
            _logger.LogWarning(ex, "Chrome DevTools connection ended");
        }
        finally
        {
            foreach (var pair in _pending)
                if (_pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(closed);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _socket?.Dispose();
        if (_receiveTask is not null) await _receiveTask;
        _connectGate.Dispose();
        _sendGate.Dispose();
    }
}
