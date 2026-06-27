namespace LocalMcp.Contracts.Results;

public sealed record ProjectVerifyResult
{
    public required string WorkingDirectory { get; init; }
    public required string DetectedProjectType { get; init; }
    public required string DetectedToolchain { get; init; }
    public required List<string> RequestedSteps { get; init; }
    public required List<ProjectVerifyStepResult> Steps { get; init; }
    public bool Success { get; init; }
    public bool TimedOut { get; init; }
    public bool Truncated { get; init; }
    public int BytesReturned { get; init; }
}

public sealed record ProjectVerifyStepResult
{
    public required string Name { get; init; }
    public required string Toolchain { get; init; }
    public required string DisplayCommand { get; init; }
    public bool Executed { get; init; }
    public bool Success { get; init; }
    public bool Skipped { get; init; }
    public string? SkipReason { get; init; }
    public int? ExitCode { get; init; }
    public long DurationMs { get; init; }
    public required string Output { get; init; }
    public bool Truncated { get; init; }
    public bool TimedOut { get; init; }
}
