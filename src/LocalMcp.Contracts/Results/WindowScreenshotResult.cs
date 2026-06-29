namespace LocalMcp.Contracts.Results;

public sealed record WindowScreenshotResult
{
    public required string WindowHandle { get; init; }
    public required string Title { get; init; }
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required UiBounds Bounds { get; init; }
    public int OriginalWidth { get; init; }
    public int OriginalHeight { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Scaled { get; init; }
    public bool WasMinimized { get; init; }
    public required string CaptureMethod { get; init; }
    public string MimeType { get; init; } = "image/png";
    public int ByteLength { get; init; }
    public required string Sha256 { get; init; }
    public required string PngBase64 { get; init; }
}
