using System;

namespace LocalMcp.Contracts.Results;

public sealed class StatResult
{
    public bool Exists { get; set; }
    public string? Type { get; set; } // "file" or "directory"
    public long? Size { get; set; }
    public string? Sha256 { get; set; }
    public string? Encoding { get; set; }
    public bool ReadOnly { get; set; }
    public DateTime? LastWriteTimeUtc { get; set; }
    public bool IsReparsePoint { get; set; }
    public bool ContentMetadataAvailable { get; set; }
    public bool ContentMetadataSkipped { get; set; }
    public string? ContentMetadataErrorCode { get; set; }
}
