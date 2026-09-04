namespace LocalMcp.Contracts.Results;

public sealed record AndroidDeviceStateResult
{
    public required string Serial { get; init; }
    public required string State { get; init; }
    public required string Manufacturer { get; init; }
    public required string Model { get; init; }
    public required string AndroidVersion { get; init; }
    public required string SdkVersion { get; init; }
    public required string ScreenSize { get; init; }
    public required string CurrentPackage { get; init; }
    public required string CurrentActivity { get; init; }
}

public sealed record AndroidScreenshotResult
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string MimeType { get; init; } = "image/png";
    public int ByteLength { get; init; }
    public required string Sha256 { get; init; }
    public required string PngBase64 { get; init; }
}

public sealed record AndroidUiTreeResult
{
    public required string Xml { get; init; }
    public int CharacterCount { get; init; }
    public bool Truncated { get; init; }
}

public sealed record AndroidInputResult
{
    public required string Action { get; init; }
    public bool Applied { get; init; }
}

public sealed record AndroidOpenAppResult
{
    public required string PackageName { get; init; }
    public string? Activity { get; init; }
    public bool Started { get; init; }
}
