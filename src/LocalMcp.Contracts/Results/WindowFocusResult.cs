namespace LocalMcp.Contracts.Results;
public sealed record WindowFocusResult
{
    public required string WindowHandle { get; init; }
    public required string PreviousForegroundWindow { get; init; }
    public bool WasMinimized { get; init; }
    public bool Restored { get; init; }
    public bool IsForeground { get; init; }
}