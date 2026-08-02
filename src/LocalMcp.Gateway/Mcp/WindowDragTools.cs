using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalMcp.Gateway.Mcp;

public sealed partial class WindowCoordinateTools
{
    [McpServerTool(Name = "window_drag", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Drags between two validated points relative to a Windows window. Requires dev:execute scope.")]
    public async Task<CallToolResult> DragWindowAsync(
        [Description("Optional internal target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("The target native window handle")] string windowHandle,
        [Description("Horizontal start coordinate relative to the window")] int startX,
        [Description("Vertical start coordinate relative to the window")] int startY,
        [Description("Horizontal end coordinate relative to the initial window bounds")] int endX,
        [Description("Vertical end coordinate relative to the initial window bounds")] int endY,
        [Description("Mouse button: left, right, or middle")] string button = WindowMouseButtons.Left,
        [Description("Total duration in milliseconds, from 0 to 10000")] int durationMs = 300,
        [Description("Interpolated movement steps, from 1 to 240")] int steps = 20,
        [Description("Optional exact process id guard")] int? expectedProcessId = null,
        [Description("Optional exact window title guard")] string? expectedWindowTitle = null)
    {
        if (!await AuthorizedAsync())
            return Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
        if (string.IsNullOrWhiteSpace(windowHandle) || windowHandle.Length > 32 || windowHandle.Any(char.IsControl))
            return Error("INVALID_REQUEST", "windowHandle is invalid.");
        if (startX is < 0 or > 100000 || startY is < 0 or > 100000)
            return Error("INVALID_REQUEST", "startX and startY must be between 0 and 100000.");
        if (endX is < -100000 or > 100000 || endY is < -100000 or > 100000)
            return Error("INVALID_REQUEST", "endX and endY must be between -100000 and 100000.");

        var normalizedButton = button?.Trim().ToLowerInvariant();
        if (!WindowMouseButtons.IsSupported(normalizedButton))
            return Error("INVALID_REQUEST", "button must be left, right, or middle.");
        if (durationMs is < 0 or > 10000)
            return Error("INVALID_REQUEST", "durationMs must be between 0 and 10000.");
        if (steps is < 1 or > 240)
            return Error("INVALID_REQUEST", "steps must be between 1 and 240.");
        if (expectedProcessId is <= 0)
            return Error("INVALID_REQUEST", "expectedProcessId must be greater than zero when provided.");
        if (expectedWindowTitle is not null &&
            (expectedWindowTitle.Length > 1024 || expectedWindowTitle.Any(char.IsControl)))
        {
            return Error("INVALID_REQUEST", "expectedWindowTitle is invalid.");
        }

        var command = new WindowDragCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = deviceId ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            WindowHandle = windowHandle,
            StartX = startX,
            StartY = startY,
            EndX = endX,
            EndY = endY,
            Button = normalizedButton!,
            DurationMs = durationMs,
            Steps = steps,
            ExpectedProcessId = expectedProcessId,
            ExpectedWindowTitle = expectedWindowTitle
        };

        try
        {
            var result = await _dispatcher.SendAsync<WindowDragResult>(command, CancellationToken());
            if (result.Success && result.Data is not null)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result.Data, JsonOptions.Default) }],
                    IsError = false
                };
            }

            return Error(result.Error?.Code ?? "INTERNAL_ERROR", result.Error?.Message ?? "Command execution failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected window drag failure for device {DeviceId}", deviceId);
            return Error("INTERNAL_ERROR", "An unexpected error occurred on the gateway.");
        }
    }
}

