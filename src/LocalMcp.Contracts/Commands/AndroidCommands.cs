namespace LocalMcp.Contracts.Commands;

public sealed record AndroidGetStateCommand : AgentCommand;

public sealed record AndroidScreenshotCommand : AgentCommand;

public sealed record AndroidUiTreeCommand : AgentCommand
{
    public int MaxCharacters { get; init; } = 200_000;
}

public sealed record AndroidTapCommand : AgentCommand
{
    public int X { get; init; }
    public int Y { get; init; }
}

public sealed record AndroidSwipeCommand : AgentCommand
{
    public int StartX { get; init; }
    public int StartY { get; init; }
    public int EndX { get; init; }
    public int EndY { get; init; }
    public int DurationMs { get; init; } = 300;
}

public sealed record AndroidTypeTextCommand : AgentCommand
{
    public required string Text { get; init; }
}

public sealed record AndroidPressKeyCommand : AgentCommand
{
    public required string KeyCode { get; init; }
}

public sealed record AndroidOpenAppCommand : AgentCommand
{
    public required string PackageName { get; init; }
    public string? Activity { get; init; }
}
