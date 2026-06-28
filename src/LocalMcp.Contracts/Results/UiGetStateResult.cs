using System.Text.Json.Serialization;

namespace LocalMcp.Contracts.Results;

public sealed record UiGetStateResult
{
    public required string WindowHandle { get; init; }
    public required string Name { get; init; }
    public required string AutomationId { get; init; }
    public required string ControlType { get; init; }
    public required UiBounds Bounds { get; init; }
    public bool Enabled { get; init; }
    public bool Focused { get; init; }
    public bool Offscreen { get; init; }
    public bool KeyboardFocusable { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? Selected { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? Checked { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? CheckState { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? Expanded { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ExpandCollapseState { get; init; }
    public int OccurrenceIndex { get; init; }
}
