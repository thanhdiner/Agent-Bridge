namespace LocalMcp.Contracts.Commands;

public sealed record UiGridReadCommand : AgentCommand
{
    public required string WindowHandle { get; init; }
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public int OccurrenceIndex { get; init; }
    public int RowStart { get; init; }
    public int RowCount { get; init; } = 50;
    public int ColumnStart { get; init; }
    public int ColumnCount { get; init; } = 20;
    public int MaxCells { get; init; } = 1000;
    public bool FocusWindow { get; init; }
}
