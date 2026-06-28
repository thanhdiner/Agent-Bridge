namespace LocalMcp.Contracts.Results;
public sealed record WindowMoveResult
{
    public required string WindowHandle { get; init; }
    public bool WasMinimized { get; init; }
    public bool WasMaximized { get; init; }
    public bool Restored { get; init; }
    public required UiBounds Bounds { get; init; }
}