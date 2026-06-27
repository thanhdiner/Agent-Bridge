using System.Text.Json;
using LocalMcp.Agent.Windows.Commands;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.PowerShell;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace LocalMcp.UnitTests;

/// <summary>
/// Unit tests for the PowerShell session management infrastructure:
/// <see cref="PowerShellSessionRegistry"/>, <see cref="PowerShellSessionState"/>,
/// and the session-routing methods in <see cref="CommandHandler"/>.
/// </summary>
public sealed class PowerShellSessionTests
{
    // ──────────────────────────────────────────────
    // Factory helpers
    // ──────────────────────────────────────────────

    private static PowerShellSessionRegistry BuildRegistry() =>
        new(NullLogger<PowerShellSessionRegistry>());

    private static ILogger<T> NullLogger<T>() =>
        new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider()
            .GetRequiredService<ILogger<T>>();

    private static CommandHandler BuildHandler(PowerShellSessionRegistry registry)
    {
        var pathPolicy = Substitute.For<IPathPolicy>();
        // Authorize any path as valid
        pathPolicy.AuthorizeCreateDirectory(
            Arg.Any<string>(),
            out Arg.Any<string>(),
            Arg.Any<bool>())
            .Returns(callInfo =>
            {
                callInfo[1] = callInfo.ArgAt<string>(0); // normalizedPath = input
                return (CommandError?)null;
            });

        var fileSystem = Substitute.For<IFileSystemExecutor>();
        var executor = new PowerShellSessionExecutor(registry, NullLogger<PowerShellSessionExecutor>());

        return new CommandHandler(
            pathPolicy,
            fileSystem,
            Substitute.For<IDirectoryCopyExecutor>(),
            registry,
            executor,
            NullLogger<CommandHandler>());
    }

    // ──────────────────────────────────────────────
    // PowerShellSessionRegistry tests
    // ──────────────────────────────────────────────

    [Fact]
    public void Registry_TryCreate_ReturnsSessionWithRunningState()
    {
        var registry = BuildRegistry();
        var session = registry.TryCreate("dev-1", maxOutputBytes: 65_536, out var error);

        Assert.NotNull(session);
        Assert.Null(error);
        Assert.Equal(PowerShellSessionStateValue.Running, session!.State);
        Assert.Equal("dev-1", session.DeviceId);
        Assert.NotEqual(Guid.Empty, session.SessionId);
    }

    [Fact]
    public void Registry_ConcurrencyLimit_RejectsWhenFull()
    {
        var registry = BuildRegistry();
        var sessions = new List<PowerShellSessionState>();

        // Fill up to the max concurrent sessions
        for (var i = 0; i < PowerShellSessionRegistry.MaxConcurrentSessions; i++)
        {
            var session = registry.TryCreate("dev-1", maxOutputBytes: 1024, out var err);
            Assert.NotNull(session);
            Assert.Null(err);
            sessions.Add(session!);
        }

        // The next one should be rejected
        var overflow = registry.TryCreate("dev-1", maxOutputBytes: 1024, out var overflowErr);
        Assert.Null(overflow);
        Assert.NotNull(overflowErr);
        Assert.Contains("maximum", overflowErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_TtlCleanup_RemovesExpiredSessions()
    {
        var registry = BuildRegistry();

        var session = registry.TryCreate("dev-1", maxOutputBytes: 1024, out _);
        Assert.NotNull(session);

        // Transition to terminal state
        session!.TryTransition(PowerShellSessionStateValue.Completed, exitCode: 0);

        // Set expiry in the past (simulating TTL elapsed)
        session.SetExpiry(DateTimeOffset.UtcNow.AddSeconds(-1));

        // Creating a new session should trigger cleanup and remove the expired one
        registry.TryCreate("dev-2", maxOutputBytes: 1024, out _);

        // Expired session should be gone
        Assert.Null(registry.Get(session.SessionId));
    }

    [Fact]
    public void Registry_CancelAll_CancelsRunningSessionCts()
    {
        var registry = BuildRegistry();
        var session = registry.TryCreate("dev-1", maxOutputBytes: 1024, out _);
        Assert.NotNull(session);
        Assert.False(session!.Cts.IsCancellationRequested);

        registry.CancelAll();

        Assert.True(session.Cts.IsCancellationRequested);
    }

    [Fact]
    public void Registry_CancelAll_IdempotentOnAlreadyTerminated()
    {
        var registry = BuildRegistry();
        var session = registry.TryCreate("dev-1", maxOutputBytes: 1024, out _);
        session!.TryTransition(PowerShellSessionStateValue.Completed, exitCode: 0);

        // Should not throw
        var exception = Record.Exception(() => registry.CancelAll());
        Assert.Null(exception);
    }

    [Fact]
    public void Registry_Get_ReturnsNullForUnknownSession()
    {
        var registry = BuildRegistry();
        Assert.Null(registry.Get(Guid.NewGuid()));
    }

    // ──────────────────────────────────────────────
    // PowerShellSessionState output buffer tests
    // ──────────────────────────────────────────────

    [Fact]
    public void SessionState_AppendStdout_IsReadableAtOffset()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", maxOutputBytes: 4096);

        var data = "Hello, World!"u8.ToArray();
        session.AppendStdout(data, data.Length);

        var snapshot = session.ReadOutput(0, 4096);
        Assert.Equal("Hello, World!", System.Text.Encoding.UTF8.GetString(snapshot.StdoutBytes));
        Assert.Equal(data.Length, snapshot.NextOffset);
        Assert.False(snapshot.StdoutTruncated);
    }

    [Fact]
    public void SessionState_OutputCappedAtMaxOutputBytes()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", maxOutputBytes: 16);

        // Write 100 bytes; only first ~8 (half of 16) fit in stdout buffer
        var large = new byte[100];
        Array.Fill(large, (byte)'A');
        session.AppendStdout(large, large.Length);

        var snapshot = session.ReadOutput(0, 16);
        Assert.True(snapshot.StdoutTruncated);
        Assert.True(snapshot.StdoutBytes.Length <= 8);
    }

    [Fact]
    public void SessionState_IncrementalReadWithOffset()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", maxOutputBytes: 4096);

        var chunk1 = "First"u8.ToArray();
        var chunk2 = "Second"u8.ToArray();
        session.AppendStdout(chunk1, chunk1.Length);
        session.AppendStdout(chunk2, chunk2.Length);

        // Read from offset 0 — should see everything
        var snap1 = session.ReadOutput(0, 4096);
        var text1 = System.Text.Encoding.UTF8.GetString(snap1.StdoutBytes);
        Assert.Equal("FirstSecond", text1);
    }

    // ──────────────────────────────────────────────
    // State machine tests
    // ──────────────────────────────────────────────

    [Fact]
    public void SessionState_TryTransition_CompletedFromRunning()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", maxOutputBytes: 1024);
        Assert.Equal(PowerShellSessionStateValue.Running, session.State);

        var won = session.TryTransition(PowerShellSessionStateValue.Completed, exitCode: 0);
        Assert.True(won);
        Assert.Equal(PowerShellSessionStateValue.Completed, session.State);
        Assert.Equal(0, session.ExitCode);
        Assert.NotNull(session.CompletedAt);
    }

    [Fact]
    public void SessionState_TryTransition_IdempotentOnTerminalState()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", maxOutputBytes: 1024);
        session.TryTransition(PowerShellSessionStateValue.Completed, exitCode: 0);

        // Second transition should lose the race
        var won = session.TryTransition(PowerShellSessionStateValue.Cancelled);
        Assert.False(won);
        // State must still be Completed
        Assert.Equal(PowerShellSessionStateValue.Completed, session.State);
    }

    // ──────────────────────────────────────────────
    // CommandHandler routing tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CommandHandler_PsStart_RegistryNullReturnsInternalError()
    {
        // Build a handler without session infrastructure (using original constructor)
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var pathPolicy = Substitute.For<IPathPolicy>();
        var fileSystem = Substitute.For<IFileSystemExecutor>();
        var handler = new CommandHandler(
            pathPolicy, fileSystem, NullLogger<CommandHandler>());

        var command = new PowerShellStartCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkingDirectory = "C:\\repo",
            Script = "echo hello"
        };

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InternalError, result.Error?.Code);
    }

    [Fact]
    public async Task CommandHandler_PsStatus_UnknownSession_ReturnsError()
    {
        var registry = BuildRegistry();
        var handler = BuildHandler(registry);

        var command = new PowerShellStatusCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = Guid.NewGuid() // unknown
        };

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("SESSION_NOT_FOUND", result.Error?.Code);
    }

    [Fact]
    public async Task CommandHandler_PsStatus_WrongDeviceId_ReturnsNotFound()
    {
        var registry = BuildRegistry();
        var session = registry.TryCreate("dev-1", maxOutputBytes: 4096, out _);
        Assert.NotNull(session);
        session!.TryTransition(PowerShellSessionStateValue.Completed, exitCode: 0);
        registry.OnSessionTerminated(session);

        var handler = BuildHandler(registry);

        var command = new PowerShellStatusCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-WRONG", // different device
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = session.SessionId
        };

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("SESSION_NOT_FOUND", result.Error?.Code);
    }

    [Fact]
    public async Task CommandHandler_PsCancel_Idempotent_OnCompletedSession()
    {
        var registry = BuildRegistry();
        var session = registry.TryCreate("dev-1", maxOutputBytes: 4096, out _);
        Assert.NotNull(session);
        session!.TryTransition(PowerShellSessionStateValue.Completed, exitCode: 0);
        registry.OnSessionTerminated(session);

        var handler = BuildHandler(registry);

        var command = new PowerShellCancelCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = session.SessionId
        };

        // First cancel
        var result1 = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result1.Success);

        // Second cancel — should also succeed (idempotent)
        var result2 = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result2.Success);

        var payload = result2.Data.Deserialize<PowerShellSessionResult>(
            LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);
        Assert.Equal("completed", payload!.State);
    }

    [Fact]
    public async Task CommandHandler_PsStatus_ReturnsCorrectState()
    {
        var registry = BuildRegistry();
        var session = registry.TryCreate("dev-1", maxOutputBytes: 4096, out _);
        Assert.NotNull(session);

        // Simulate some output
        var output = "Hello from PowerShell"u8.ToArray();
        session!.AppendStdout(output, output.Length);
        session.TryTransition(PowerShellSessionStateValue.Completed, exitCode: 0);
        registry.OnSessionTerminated(session);

        var handler = BuildHandler(registry);

        var command = new PowerShellStatusCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = session.SessionId,
            OutputOffset = 0,
            MaxOutputBytes = 65_536
        };

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result.Success);

        var payload = result.Data.Deserialize<PowerShellSessionResult>(
            LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);
        Assert.NotNull(payload);
        Assert.Equal("completed", payload!.State);
        Assert.Equal("Hello from PowerShell", payload.Stdout);
        Assert.Equal(0, payload.ExitCode);
        Assert.False(payload.Truncated);
    }

    [Fact]
    public async Task CommandHandler_PsStart_InvalidScript_Empty_ReturnsError()
    {
        var registry = BuildRegistry();
        var handler = BuildHandler(registry);

        var command = new PowerShellStartCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
            Script = "" // invalid
        };

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task CommandHandler_PsStart_TimeoutOutOfRange_ReturnsError()
    {
        var registry = BuildRegistry();
        var handler = BuildHandler(registry);

        var command = new PowerShellStartCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
            Script = "echo hello",
            TimeoutSeconds = 0 // invalid: must be >= 1
        };

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task CommandHandler_PsStart_ElevatedSession_ReturnsError()
    {
        var registry = BuildRegistry();
        var handler = BuildHandler(registry);

        var command = new PowerShellStartCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
            Script = "echo hello",
            Elevated = true,
            Visible = true // elevated sessions require visible=true first, but then fail on async check
        };

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
        Assert.Contains("elevated", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CommandHandler_PsCancel_CancelsSetsCtsCancelled()
    {
        var registry = BuildRegistry();
        var session = registry.TryCreate("dev-1", maxOutputBytes: 4096, out _);
        Assert.NotNull(session);
        Assert.Equal(PowerShellSessionStateValue.Running, session!.State);
        Assert.False(session.Cts.IsCancellationRequested);

        var handler = BuildHandler(registry);

        var command = new PowerShellCancelCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = session.SessionId
        };

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result.Success);

        // CTS must be cancelled
        Assert.True(session.Cts.IsCancellationRequested);
    }

    // ──────────────────────────────────────────────
    // AgentCommandTimeouts tests
    // ──────────────────────────────────────────────

    [Fact]
    public void AgentCommandTimeouts_PsStart_Returns15Seconds()
    {
        var cmd = new PowerShellStartCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkingDirectory = "C:\\repo",
            Script = "echo hello"
        };

        Assert.Equal(TimeSpan.FromSeconds(15), AgentCommandTimeouts.GetTimeout(cmd));
    }

    [Fact]
    public void AgentCommandTimeouts_PsStatus_Returns10Seconds()
    {
        var cmd = new PowerShellStatusCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = Guid.NewGuid()
        };

        Assert.Equal(TimeSpan.FromSeconds(10), AgentCommandTimeouts.GetTimeout(cmd));
    }

    [Fact]
    public void AgentCommandTimeouts_PsCancel_Returns10Seconds()
    {
        var cmd = new PowerShellCancelCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = Guid.NewGuid()
        };

        Assert.Equal(TimeSpan.FromSeconds(10), AgentCommandTimeouts.GetTimeout(cmd));
    }
}
