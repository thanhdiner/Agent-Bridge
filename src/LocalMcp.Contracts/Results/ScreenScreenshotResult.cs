namespace LocalMcp.Contracts.Results;

public sealed record ScreenScreenshotResult
{
    public required string CaptureMode { get; init; }
    public int? SelectedMonitorIndex { get; init; }
    public required UiBounds Bounds { get; init; }
    public required UiBounds VirtualScreenBounds { get; init; }
    public required IReadOnlyList<ScreenMonitorInfo> Monitors { get; init; }
    public int OriginalWidth { get; init; }
    public int OriginalHeight { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Scaled { get; init; }
    public required string CaptureMethod { get; init; }
    public string MimeType { get; init; } = "image/png";
    public int ByteLength { get; init; }
    public required string Sha256 { get; init; }
    public required string PngBase64 { get; init; }
}

public sealed record ScreenMonitorInfo
{
    public int Index { get; init; }
    public required string DeviceName { get; init; }
    public bool IsPrimary { get; init; }
    public required UiBounds Bounds { get; init; }
    public required UiBounds WorkArea { get; init; }
    public uint DpiX { get; init; }
    public uint DpiY { get; init; }
    public double ScaleFactor { get; init; }
}
