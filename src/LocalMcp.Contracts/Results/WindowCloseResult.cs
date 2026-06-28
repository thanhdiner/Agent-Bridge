namespace LocalMcp.Contracts.Results;
public sealed record WindowCloseResult
{
    public required string WindowHandle { get; init; }
    public int ProcessId { get; init; }
    public bool CloseRequested { get; init; }
    public bool Closed { get; init; }
}