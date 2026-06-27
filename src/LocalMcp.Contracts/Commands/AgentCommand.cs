namespace LocalMcp.Contracts.Commands;

public abstract record AgentCommand
{
    public required Guid CommandId { get; init; }
    public required string DeviceId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string CommandType => GetType().Name;
}
