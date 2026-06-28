using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using LocalMcp.Agent.Windows.Commands;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.PowerShell;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.Gateway.Mcp;
using LocalMcp.Gateway.Commands;

namespace LocalMcp.UnitTests;

public sealed class PowerShellSessionTests
{
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
        pathPolicy.AuthorizeCreateDirectory(
            Arg.Any<string>(),
            out Arg.Any<string>(),
            Arg.Any<bool>())
            .Returns(callInfo =>
            {
                callInfo[1] = callInfo.ArgAt<string>(0);
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

    private static string? GetPwshPath()
    {
        var path = FileSystemExecutor.ResolveToolExecutable("pwsh.exe", AppDomain.CurrentDomain.BaseDirectory);
        Console.WriteLine($"[TEST-LOG] GetPwshPath resolved: '{path ?? "null"}'");
        return path;
    }

    public class PwshFactAttribute : FactAttribute
    {
        public PwshFactAttribute()
        {
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            var path = FileSystemExecutor.ResolveToolExecutable("pwsh.exe", AppDomain.CurrentDomain.BaseDirectory);
            if (!isWindows && path == null)
            {
                Skip = "pwsh.exe is not available on this non-Windows system.";
            }
        }
    }

    [Fact]
    public void ResolveToolExecutable_NormalExecutable_Accepted()
    {
        FileSystemExecutor.FileAttributesOverrideForTest = path =>
            path.EndsWith("dummy.exe") ? FileAttributes.Normal : null;
        FileSystemExecutor.ReparseTagOverrideForTest = null;

        try
        {
            var originalPath = Environment.GetEnvironmentVariable("PATH");
            var dummyDir = AppDomain.CurrentDomain.BaseDirectory;
            var workingDir = Path.Combine(dummyDir, "test_working_dir");
            Environment.SetEnvironmentVariable("PATH", dummyDir);

            try
            {
                var resolved = FileSystemExecutor.ResolveToolExecutable("dummy.exe", workingDir);
                Assert.NotNull(resolved);
                Assert.EndsWith("dummy.exe", resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        }
        finally
        {
            FileSystemExecutor.FileAttributesOverrideForTest = null;
        }
    }

    [Fact]
    public void ResolveToolExecutable_AppExecutionAlias_Accepted()
    {
        FileSystemExecutor.FileAttributesOverrideForTest = path =>
            path.EndsWith("dummy.exe") ? FileAttributes.ReparsePoint : null;
        FileSystemExecutor.ReparseTagOverrideForTest = path =>
            path.EndsWith("dummy.exe") ? 0x8000001Bu : null;

        try
        {
            var originalPath = Environment.GetEnvironmentVariable("PATH");
            var dummyDir = AppDomain.CurrentDomain.BaseDirectory;
            var workingDir = Path.Combine(dummyDir, "test_working_dir");
            Environment.SetEnvironmentVariable("PATH", dummyDir);

            try
            {
                var resolved = FileSystemExecutor.ResolveToolExecutable("dummy.exe", workingDir);
                Assert.NotNull(resolved);
                Assert.EndsWith("dummy.exe", resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        }
        finally
        {
            FileSystemExecutor.FileAttributesOverrideForTest = null;
            FileSystemExecutor.ReparseTagOverrideForTest = null;
        }
    }

    [Fact]
    public void ResolveToolExecutable_ArbitrarySymlink_Rejected()
    {
        FileSystemExecutor.FileAttributesOverrideForTest = path =>
            path.EndsWith("dummy.exe") ? FileAttributes.ReparsePoint : null;
        FileSystemExecutor.ReparseTagOverrideForTest = path =>
            path.EndsWith("dummy.exe") ? 0xA000000Cu : null; // Arbitrary symlink tag

        try
        {
            var originalPath = Environment.GetEnvironmentVariable("PATH");
            var dummyDir = AppDomain.CurrentDomain.BaseDirectory;
            var workingDir = Path.Combine(dummyDir, "test_working_dir");
            Environment.SetEnvironmentVariable("PATH", dummyDir);

            try
            {
                var resolved = FileSystemExecutor.ResolveToolExecutable("dummy.exe", workingDir);
                Assert.Null(resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        }
        finally
        {
            FileSystemExecutor.FileAttributesOverrideForTest = null;
            FileSystemExecutor.ReparseTagOverrideForTest = null;
        }
    }

    [Fact]
    public void ResolveToolExecutable_DirectoryReparsePoint_Rejected()
    {
        FileSystemExecutor.FileAttributesOverrideForTest = path =>
            path.EndsWith("dummy.exe") ? (FileAttributes.ReparsePoint | FileAttributes.Directory) : null;
        FileSystemExecutor.ReparseTagOverrideForTest = path =>
            path.EndsWith("dummy.exe") ? 0x8000001Bu : null; // App execution alias but it's a directory

        try
        {
            var originalPath = Environment.GetEnvironmentVariable("PATH");
            var dummyDir = AppDomain.CurrentDomain.BaseDirectory;
            var workingDir = Path.Combine(dummyDir, "test_working_dir");
            Environment.SetEnvironmentVariable("PATH", dummyDir);

            try
            {
                var resolved = FileSystemExecutor.ResolveToolExecutable("dummy.exe", workingDir);
                Assert.Null(resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        }
        finally
        {
            FileSystemExecutor.FileAttributesOverrideForTest = null;
            FileSystemExecutor.ReparseTagOverrideForTest = null;
        }
    }

    [Fact]
    public void SessionState_Utf8Streaming_DoesNotCorruptHalfEmoji()
    {
        // 🌟 is encoded in UTF-8 as 4 bytes: 0xF0, 0x9F, 0x8C, 0x9F
        var part1 = new byte[] { 0xF0, 0x9F };
        var part2 = new byte[] { 0x8C, 0x9F };

        var state = new PowerShellSessionState(Guid.NewGuid(), "dev", 1024);
        state.AppendStdout(part1, part1.Length);

        // Read output: should be empty because 🌟 is incomplete
        var snap1 = state.ReadOutput(0, 0, 100);
        Assert.Empty(snap1.StdoutBytes);

        // Append rest of the emoji
        state.AppendStdout(part2, part2.Length);

        // Read output: should contain the full 4-byte 🌟 emoji
        var snap2 = state.ReadOutput(0, 0, 100);
        Assert.Equal(4, snap2.StdoutBytes.Length);
        Assert.Equal(0xF0, snap2.StdoutBytes[0]);
        Assert.Equal(0x9F, snap2.StdoutBytes[1]);
        Assert.Equal(0x8C, snap2.StdoutBytes[2]);
        Assert.Equal(0x9F, snap2.StdoutBytes[3]);
    }

    [Fact]
    public async Task SessionState_PublicationOrdering_ValidStateAfterOutputs()
    {
        var state = new PowerShellSessionState(Guid.NewGuid(), "dev", 1024);
        var part1 = new byte[] { 0xF0, 0x9F }; // Incomplete emoji
        state.AppendStdout(part1, part1.Length);

        // Transition should finalize and complete the task
        bool transitioned = state.TryTransition(PowerShellSessionStateValue.Completed, 0);
        Assert.True(transitioned);

        // Verify task is completed
        await state.CompletionTask;

        // Verify pending bytes were discarded on transition, so we don't output corrupt half emoji
        var snap = state.ReadOutput(0, 0, 100);
        Assert.Empty(snap.StdoutBytes);
    }

    [Fact]
    public void SessionState_OrphanByte_Discarded()
    {
        // 0x80 is an invalid continuation byte on its own
        var invalidBytes = new byte[] { 0x80, 0x41 }; // 0x80 (invalid), 0x41 ('A')
        var state = new PowerShellSessionState(Guid.NewGuid(), "dev", 1024);
        state.AppendStdout(invalidBytes, invalidBytes.Length);

        var snap = state.ReadOutput(0, 0, 100);
        // Should discard the invalid 0x80 and only output 'A' (0x41)
        Assert.Single(snap.StdoutBytes);
        Assert.Equal(0x41, snap.StdoutBytes[0]);
    }

    [Fact]
    public void SessionState_MalformedUtf8_Filtered()
    {
        // 0xC0 0xAF is overlong encoding for '/' (malformed UTF-8)
        var malformed = new byte[] { 0xC0, 0xAF, 0x42 }; // 0xC0 0xAF (malformed), 0x42 ('B')
        var state = new PowerShellSessionState(Guid.NewGuid(), "dev", 1024);
        state.AppendStdout(malformed, malformed.Length);

        var snap = state.ReadOutput(0, 0, 100);
        // Should filter/discard the malformed sequence and only output 'B' (0x42)
        Assert.Single(snap.StdoutBytes);
        Assert.Equal(0x42, snap.StdoutBytes[0]);
    }

    [Fact]
    public async Task SessionState_PollAfterCompletionTask_ReturnsFinalSnapshot()
    {
        var state = new PowerShellSessionState(Guid.NewGuid(), "dev", 1024);
        state.TryTransition(PowerShellSessionStateValue.Completed, 0);

        await state.CompletionTask;

        var snapshot = state.GetSnapshot();
        Assert.Equal(PowerShellSessionStateValue.Completed, snapshot.State);
        Assert.Equal(0, snapshot.ExitCode);
    }

    // ──────────────────────────────────────────────
    // 1. Thread-safety & Concurrency / Eviction Race
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Registry_ConcurrentLimitRace_Exactly16Created()
    {
        var registry = BuildRegistry();
        var tasks = new List<Task<PowerShellSessionState?>>();

        for (int i = 0; i < 32; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                string? error;
                return registry.TryCreate("dev-1", 1024, out error);
            }));
        }

        var results = await Task.WhenAll(tasks);
        var createdCount = results.Count(r => r != null);
        var rejectedCount = results.Count(r => r == null);

        Assert.Equal(16, createdCount);
        Assert.Equal(16, rejectedCount);

        var runningCount = registry.Sessions.Count(s => s.State == PowerShellSessionStateValue.Running);
        Assert.True(runningCount <= 16);
    }

    [Fact]
    public async Task Registry_HistoryLimitRace_NeverExceeds100()
    {
        var registry = BuildRegistry();
        var originalSessions = new List<PowerShellSessionState>();

        // Populate 100 finished sessions
        for (int i = 0; i < 100; i++)
        {
            var session = registry.TryCreate("dev-1", 1024, out _);
            Assert.NotNull(session);
            session!.TryTransition(PowerShellSessionStateValue.Completed, 0);
            registry.OnSessionTerminated(session);
            originalSessions.Add(session);
        }

        Assert.Equal(100, registry.Count);

        // Try to concurrently add 50 more
        var tasks = new List<Task<PowerShellSessionState?>>();
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                string? error;
                return registry.TryCreate("dev-1", 1024, out error);
            }));
        }

        var results = await Task.WhenAll(tasks);
        var successfullyCreated = results.Where(r => r != null).ToList();

        // Assert exact Count and Running limits
        Assert.Equal(100, registry.Count);
        var runningCount = registry.Sessions.Count(s => s.State == PowerShellSessionStateValue.Running);
        Assert.True(runningCount <= 16);

        // Assert that the oldest K terminal sessions were evicted, and the remaining 100-K were retained
        int K = successfullyCreated.Count;
        Assert.True(K > 0, "At least some concurrent sessions must have been created.");
        for (int i = 0; i < 100; i++)
        {
            var orig = originalSessions[i];
            var inRegistry = registry.Get(orig.SessionId) != null;
            if (i < K)
            {
                Assert.False(inRegistry, $"Session at index {i} (StartedAt: {orig.StartedAt:o}) should be evicted.");
            }
            else
            {
                Assert.True(inRegistry, $"Session at index {i} (StartedAt: {orig.StartedAt:o}) should be retained.");
            }
        }

        // Assert the newly created ones actually exist in the registry
        var currentSessions = registry.Sessions.ToHashSet();
        foreach (var newSession in successfullyCreated)
        {
            Assert.Contains(newSession!, currentSessions);
        }

        // Clean up
        registry.Dispose();
    }

    // ──────────────────────────────────────────────
    // 2. TTL Lookup Enforcement & Disposal
    // ──────────────────────────────────────────────

    [Fact]
    public void Registry_TtlLookup_EvictsAndDisposesWithoutTryCreate()
    {
        var registry = BuildRegistry();
        var session = registry.TryCreate("dev-1", 1024, out _);
        Assert.NotNull(session);

        session!.TryTransition(PowerShellSessionStateValue.Completed, 0);
        registry.OnSessionTerminated(session);

        // Expire it manually
        session.SetExpiry(DateTimeOffset.UtcNow.AddMinutes(-5));

        // Registry.Get should check expiry, evict and dispose
        var retrieved = registry.Get(session.SessionId);
        Assert.Null(retrieved);

        // Check if Cts in expired session was disposed
        Assert.Throws<ObjectDisposedException>(() => session.Cts.Token);
    }

    // ──────────────────────────────────────────────
    // 3. UTF-8 Vietnamese & Emoji Pagination
    // ──────────────────────────────────────────────

    [Fact]
    public void SessionState_Utf8Pagination_NoSplits()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 4096);
        var originalText = "Xin chào Cà Phê ☕ và Emoji 🧪";
        var bytes = System.Text.Encoding.UTF8.GetBytes(originalText);
        session.AppendStdout(bytes, bytes.Length);

        // Read in tiny chunks and reconstruct
        long offset = 0;
        var reconstructed = new System.Text.StringBuilder();

        while (true)
        {
            var snap = session.ReadOutput(offset, 0, maxBytes: 5);
            if (snap.StdoutBytes.Length == 0)
                break;

            var chunkText = System.Text.Encoding.UTF8.GetString(snap.StdoutBytes);
            Assert.DoesNotContain("\uFFFD", chunkText);
            reconstructed.Append(chunkText);
            offset = snap.NextStdoutOffset;
        }

        Assert.Equal(originalText, reconstructed.ToString());
    }

    // ──────────────────────────────────────────────
    // 4. Combined Output Retention Cap Semantics
    // ──────────────────────────────────────────────

    [Fact]
    public void SessionState_OutputRetentionCap_IsCombined()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 100);

        var data1 = new byte[80];
        Array.Fill(data1, (byte)'A');
        session.AppendStdout(data1, data1.Length);

        var data2 = new byte[50];
        Array.Fill(data2, (byte)'B');
        session.AppendStderr(data2, data2.Length);

        var snap = session.ReadOutput(0, 0, 200);
        Assert.True(snap.Truncated);
        Assert.Equal(80, snap.StdoutBytes.Length);
        Assert.Equal(20, snap.StderrBytes.Length);
    }

    // ──────────────────────────────────────────────
    // 5. Late Streams Paging Independence
    // ──────────────────────────────────────────────

    [Fact]
    public void SessionState_LateStreams_PagingIsIndependent()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 1024);

        var stdout1 = "stdout1"u8.ToArray();
        session.AppendStdout(stdout1, stdout1.Length);

        var snap1 = session.ReadOutput(0, 0, 100);
        Assert.Equal("stdout1", System.Text.Encoding.UTF8.GetString(snap1.StdoutBytes));
        Assert.Equal(stdout1.Length, snap1.NextStdoutOffset);
        Assert.Equal(0, snap1.NextStderrOffset);

        var stderr1 = "stderr1"u8.ToArray();
        session.AppendStderr(stderr1, stderr1.Length);

        var snap2 = session.ReadOutput(snap1.NextStdoutOffset, 0, 100);
        Assert.Empty(snap2.StdoutBytes);
        Assert.Equal("stderr1", System.Text.Encoding.UTF8.GetString(snap2.StderrBytes));
        Assert.Equal(snap1.NextStdoutOffset, snap2.NextStdoutOffset);
        Assert.Equal(stderr1.Length, snap2.NextStderrOffset);
    }

    // ──────────────────────────────────────────────
    // 6. State Publication Race & Consistency
    // ──────────────────────────────────────────────

    [Fact]
    public void SessionState_StateTransition_AtomicityAndConsistency()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 1024);
        
        Assert.Equal(PowerShellSessionStateValue.Running, session.State);
        Assert.Null(session.CompletedAt);
        Assert.Null(session.ExitCode);

        var transitioned = session.TryTransition(PowerShellSessionStateValue.Completed, 123);
        Assert.True(transitioned);

        Assert.Equal(PowerShellSessionStateValue.Completed, session.State);
        Assert.NotNull(session.CompletedAt);
        Assert.Equal(123, session.ExitCode);
    }

    [Fact]
    public async Task SessionState_StateTransition_NoConsistencyRace()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 1024);
        
        var t1 = Task.Run(async () =>
        {
            await Task.Delay(5);
            session.TryTransition(PowerShellSessionStateValue.Completed, 0);
        });

        var t2 = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                var snap = session.GetSnapshot();
                if (snap.State == PowerShellSessionStateValue.Completed)
                {
                    Assert.NotNull(snap.CompletedAt);
                    Assert.NotNull(snap.ExitCode);
                }
                else
                {
                    Assert.Null(snap.CompletedAt);
                    Assert.Null(snap.ExitCode);
                }
            }
        });

        await Task.WhenAll(t1, t2);
    }

    // ──────────────────────────────────────────────
    // 7. Validation & Wrong-Device Isolation
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CommandHandler_Validation_WrongDevice_Isolation()
    {
        var registry = BuildRegistry();
        var session = registry.TryCreate("dev-1", 1024, out _);
        Assert.NotNull(session);

        var handler = BuildHandler(registry);

        var statusCmd = new PowerShellStatusCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-2",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = session!.SessionId,
            StdoutOffset = 0,
            StderrOffset = 0
        };

        var statusResult = await handler.HandleAsync(statusCmd, CancellationToken.None);
        Assert.False(statusResult.Success);
        Assert.Equal("SESSION_NOT_FOUND", statusResult.Error?.Code);

        var cancelCmd = new PowerShellCancelCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-2",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = session.SessionId
        };

        var cancelResult = await handler.HandleAsync(cancelCmd, CancellationToken.None);
        Assert.False(cancelResult.Success);
        Assert.Equal("SESSION_NOT_FOUND", cancelResult.Error?.Code);
    }

    // ──────────────────────────────────────────────
    // 8. Gateway Authorization & Validation
    // ──────────────────────────────────────────────

    [Fact]
    public void Gateway_McpTools_HaveDevExecutePolicyAttributes()
    {
        var methods = typeof(PowerShellSessionTools).GetMethods();
        var toolNames = new[] { "powershell_start", "powershell_status", "powershell_cancel" };

        foreach (var name in toolNames)
        {
            var method = methods.SingleOrDefault(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), true)
                .Cast<McpServerToolAttribute>().Any(a => a.Name == name));
            Assert.NotNull(method);
        }
    }

    [Fact]
    public async Task Gateway_Authorization_Fails_ReturnsForbidden()
    {
        var dispatcher = Substitute.For<ICommandDispatcher>();
        var authService = Substitute.For<IAuthorizationService>();
        
        authService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object>(),
            Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());

        var tools = new PowerShellSessionTools(
            dispatcher,
            authService,
            NullLogger<PowerShellSessionTools>());

        var startResult = await tools.StartSessionAsync("dev-1", "C:\\repo", "echo hello");
        Assert.True(startResult.IsError);
        Assert.Contains("FORBIDDEN", Assert.IsType<TextContentBlock>(startResult.Content[0]).Text);

        var statusResult = await tools.GetSessionStatusAsync("dev-1", Guid.NewGuid().ToString(), 0, 0, 65536);
        Assert.True(statusResult.IsError);
        Assert.Contains("FORBIDDEN", Assert.IsType<TextContentBlock>(statusResult.Content[0]).Text);

        var cancelResult = await tools.CancelSessionAsync("dev-1", Guid.NewGuid().ToString());
        Assert.True(cancelResult.IsError);
        Assert.Contains("FORBIDDEN", Assert.IsType<TextContentBlock>(cancelResult.Content[0]).Text);

        Assert.Empty(dispatcher.ReceivedCalls());
    }

    // ──────────────────────────────────────────────
    // 9. Cancel timeout & Start cancellation leak
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CommandHandler_PsCancel_Timeout_ReturnsCancelTimeout()
    {
        var registry = BuildRegistry();
        var handler = BuildHandler(registry);

        var session = registry.TryCreate("dev-1", 1024, out _);
        Assert.NotNull(session);
        Assert.Equal(PowerShellSessionStateValue.Running, session!.State);

        var cancelCmd = new PowerShellCancelCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = session.SessionId
        };

        var result = await handler.HandleAsync(cancelCmd, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CANCEL_TIMEOUT", result.Error?.Code);
    }

    [Fact]
    public async Task CommandHandler_PsStart_CancellationLeak_CleansUpAndDisposes()
    {
        var registry = BuildRegistry();
        var startCalled = false;
        var mockExecutor = new MockPowerShellSessionExecutor(registry, NullLogger<PowerShellSessionExecutor>(), () => startCalled = true);

        var pathPolicy = Substitute.For<IPathPolicy>();
        pathPolicy.AuthorizeCreateDirectory(Arg.Any<string>(), out Arg.Any<string>(), Arg.Any<bool>())
            .Returns(callInfo =>
            {
                callInfo[1] = callInfo.ArgAt<string>(0);
                return (CommandError?)null;
            });
        var fileSystem = Substitute.For<IFileSystemExecutor>();

        var handler = new CommandHandler(
            pathPolicy,
            fileSystem,
            Substitute.For<IDirectoryCopyExecutor>(),
            registry,
            mockExecutor,
            NullLogger<CommandHandler>());

        using var cts = new CancellationTokenSource();
        Guid? createdSessionId = null;

        handler.OnSessionCreatedForTest = (session) =>
        {
            if (session != null)
            {
                createdSessionId = session.SessionId;
                cts.Cancel(); // Cancel the token dynamically during execution
            }
        };

        var startCmd = new PowerShellStartCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
            Script = "echo hello"
        };

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await handler.HandleAsync(startCmd, cts.Token);
        });

        Assert.NotNull(createdSessionId);
        Assert.Equal(0, registry.Count);
        Assert.Null(registry.Get(createdSessionId.Value));
        Assert.False(startCalled, "Executor should not have been started.");
    }

    // ──────────────────────────────────────────────
    // 10. Bounded Output & Offset Edge Cases
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CommandHandler_PsStatus_MaxOutputBytes_Validation()
    {
        var registry = BuildRegistry();
        var handler = BuildHandler(registry);

        var session = registry.TryCreate("dev-1", 1024, out _);
        Assert.NotNull(session);

        var invalidCmd1 = new PowerShellStatusCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = session!.SessionId,
            MaxOutputBytes = 1
        };
        var res1 = await handler.HandleAsync(invalidCmd1, CancellationToken.None);
        Assert.False(res1.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, res1.Error?.Code);

        var invalidCmd3 = new PowerShellStatusCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = session.SessionId,
            MaxOutputBytes = 3
        };
        var res3 = await handler.HandleAsync(invalidCmd3, CancellationToken.None);
        Assert.False(res3.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, res3.Error?.Code);

        var validCmd4 = new PowerShellStatusCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = session.SessionId,
            MaxOutputBytes = 4
        };
        var res4 = await handler.HandleAsync(validCmd4, CancellationToken.None);
        Assert.True(res4.Success);
    }

    [Fact]
    public void SessionState_Offset_LongMaxValue_ReturnsCleanly()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 1024);
        var stdout = "hello"u8.ToArray();
        session.AppendStdout(stdout, stdout.Length);

        var snap = session.ReadOutput(long.MaxValue, long.MaxValue, 100);
        Assert.Empty(snap.StdoutBytes);
        Assert.Empty(snap.StderrBytes);
        Assert.Equal(5, snap.NextStdoutOffset);
        Assert.Equal(0, snap.NextStderrOffset);
    }

    [Fact]
    public void SessionState_Offset_MiddleOfEmoji_Normalizes()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 1024);
        var bytes = System.Text.Encoding.UTF8.GetBytes("🧪"); // 4 bytes
        session.AppendStdout(bytes, bytes.Length);

        var snap = session.ReadOutput(2, 0, 100); // offset = 2 points inside the 4-byte emoji
        Assert.Equal("🧪", System.Text.Encoding.UTF8.GetString(snap.StdoutBytes));
        Assert.Equal(4, snap.NextStdoutOffset);
    }

    [Fact]
    public void SessionState_RetentionCap_CutsMiddleOfEmoji_CleanTruncation()
    {
        // Combined cap of 3 bytes. We write "A" (1 byte) then "🧪" (4 bytes).
        // Total exceeds 3, so it cuts. The emoji cannot fit in the remaining 2 bytes.
        // It must cleanly truncate the emoji, leaving only "A" in buffer.
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 3);

        session.AppendStdout("A"u8.ToArray(), 1);
        session.AppendStdout(System.Text.Encoding.UTF8.GetBytes("🧪"), 4);

        var snap = session.ReadOutput(0, 0, 100);
        Assert.True(snap.Truncated);
        Assert.Equal("A", System.Text.Encoding.UTF8.GetString(snap.StdoutBytes));
    }

    [Fact]
    public void SessionState_RuneSplitAcrossAppends_DecodesCorrectly()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 1024);
        var bytes = System.Text.Encoding.UTF8.GetBytes("🧪");
        
        session.AppendStdout(bytes.Take(2).ToArray(), 2);
        session.AppendStdout(bytes.Skip(2).ToArray(), 2);

        var snap = session.ReadOutput(0, 0, 100);
        Assert.Equal("🧪", System.Text.Encoding.UTF8.GetString(snap.StdoutBytes));
    }

    [Fact]
    public void SessionState_CombinedResponse_DoesNotExceedPageBudget()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 1024);
        var stdout = "stdoutdata"u8.ToArray(); // 10 bytes
        var stderr = "stderrdata"u8.ToArray(); // 10 bytes

        session.AppendStdout(stdout, stdout.Length);
        session.AppendStderr(stderr, stderr.Length);

        // Budget = 8
        var snap = session.ReadOutput(0, 0, maxBytes: 8);
        var combinedLen = snap.StdoutBytes.Length + snap.StderrBytes.Length;
        Assert.True(combinedLen <= 8);
    }

    // ──────────────────────────────────────────────
    // 11. Restored & Historical Core Tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CommandHandler_PsStatus_UnknownSession_ReturnsNotFound()
    {
        var registry = BuildRegistry();
        var handler = BuildHandler(registry);

        var command = new PowerShellStatusCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = Guid.NewGuid()
        };

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("SESSION_NOT_FOUND", result.Error?.Code);
    }

    [Fact]
    public async Task CommandHandler_PsCancel_Idempotent_OnCompletedSession()
    {
        var registry = BuildRegistry();
        var session = registry.TryCreate("dev-1", 4096, out _);
        Assert.NotNull(session);
        session!.TryTransition(PowerShellSessionStateValue.Completed, 0);
        registry.OnSessionTerminated(session);

        var handler = BuildHandler(registry);

        var command = new PowerShellCancelCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = session.SessionId
        };

        var result1 = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result1.Success);

        var result2 = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result2.Success);

        var payload = result2.Data.Deserialize<PowerShellSessionResult>(
            LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);
        Assert.Equal("completed", payload!.State);
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
            Script = ""
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
            TimeoutSeconds = 0
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
            Visible = true
        };

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

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

    // ──────────────────────────────────────────────
    // 12. Real Integration Tests with pwsh.exe
    // ──────────────────────────────────────────────

    [PwshFact]
    public async Task Start_RealPwsh_SuccessAndStdout()
    {
        var pwshPath = GetPwshPath();
        Assert.NotNull(pwshPath);

        var registry = BuildRegistry();
        var executor = new PowerShellSessionExecutor(registry, NullLogger<PowerShellSessionExecutor>());
        var session = registry.TryCreate("dev-1", 4096, out _);
        Assert.NotNull(session);

        executor.StartBackground(
            session!,
            pwshPath!,
            AppDomain.CurrentDomain.BaseDirectory,
            "Write-Output \"Hello World\"",
            timeoutSeconds: 30);

        await session!.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PowerShellSessionStateValue.Completed, session.State);
        Assert.Equal(0, session.ExitCode);
        
        var snap = session.ReadOutput(0, 0, 4096);
        var stdoutText = System.Text.Encoding.UTF8.GetString(snap.StdoutBytes);
        Assert.Contains("Hello World", stdoutText);
    }

    [PwshFact]
    public async Task Start_RealPwsh_NonZeroExitAndStderr()
    {
        var pwshPath = GetPwshPath();
        Assert.NotNull(pwshPath);

        var registry = BuildRegistry();
        var executor = new PowerShellSessionExecutor(registry, NullLogger<PowerShellSessionExecutor>());
        var session = registry.TryCreate("dev-1", 4096, out _);
        Assert.NotNull(session);

        executor.StartBackground(
            session!,
            pwshPath!,
            AppDomain.CurrentDomain.BaseDirectory,
            "Write-Error \"Failed execution\"; exit 101",
            timeoutSeconds: 30);

        await session!.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PowerShellSessionStateValue.Failed, session.State);
        Assert.Equal(101, session.ExitCode);

        var snap = session.ReadOutput(0, 0, 4096);
        var stderrText = System.Text.Encoding.UTF8.GetString(snap.StderrBytes);
        Assert.Contains("Failed execution", stderrText);
    }

    [PwshFact]
    public async Task Start_RealPwsh_Timeout()
    {
        var pwshPath = GetPwshPath();
        Assert.NotNull(pwshPath);

        var registry = BuildRegistry();
        var executor = new PowerShellSessionExecutor(registry, NullLogger<PowerShellSessionExecutor>());
        var session = registry.TryCreate("dev-1", 4096, out _);
        Assert.NotNull(session);

        executor.StartBackground(
            session!,
            pwshPath!,
            AppDomain.CurrentDomain.BaseDirectory,
            "Start-Sleep -Seconds 10",
            timeoutSeconds: 2);

        await session!.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PowerShellSessionStateValue.TimedOut, session.State);
    }

    [PwshFact]
    public async Task Start_RealPwsh_ExternalCancelAndChildTreeKilled()
    {
        var pwshPath = GetPwshPath();
        Assert.NotNull(pwshPath);

        var registry = BuildRegistry();
        var executor = new PowerShellSessionExecutor(registry, NullLogger<PowerShellSessionExecutor>());
        var session = registry.TryCreate("dev-1", 4096, out _);
        Assert.NotNull(session);

        executor.StartBackground(
            session!,
            pwshPath!,
            AppDomain.CurrentDomain.BaseDirectory,
            "Start-Process cmd.exe -ArgumentList '/c timeout 100' -NoNewWindow -PassThru; Start-Sleep -Seconds 10",
            timeoutSeconds: 30);

        await Task.Delay(1000);

        registry.Cancel(session!);
        await session!.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PowerShellSessionStateValue.Cancelled, session.State);

        var proc = session.Process;
        if (proc != null)
        {
            Assert.True(proc.HasExited);
        }
    }

    [PwshFact]
    public async Task Start_RealPwsh_ChildProcessTreeKilled_Verified()
    {
        var pwshPath = GetPwshPath();
        Assert.NotNull(pwshPath);

        var registry = BuildRegistry();
        var executor = new PowerShellSessionExecutor(registry, NullLogger<PowerShellSessionExecutor>());
        var session = registry.TryCreate("dev-1", 4096, out _);
        Assert.NotNull(session);

        executor.StartBackground(
            session!,
            pwshPath!,
            AppDomain.CurrentDomain.BaseDirectory,
            "$p = Start-Process cmd.exe -ArgumentList '/c timeout 100' -PassThru -NoNewWindow; Write-Output \"CHILD_PID=$($p.Id)\"; Start-Sleep -Seconds 10",
            timeoutSeconds: 30);

        int childPid = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (childPid == 0 && !cts.IsCancellationRequested)
        {
            var snap = session.ReadOutput(0, 0, 4096);
            var text = System.Text.Encoding.UTF8.GetString(snap.StdoutBytes);
            if (text.Contains("CHILD_PID="))
            {
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var match = lines.FirstOrDefault(l => l.StartsWith("CHILD_PID="));
                if (match != null)
                {
                    childPid = int.Parse(match.Substring("CHILD_PID=".Length));
                }
            }
            await Task.Delay(100);
        }

        Assert.True(childPid > 0, "Failed to get child PID");

        registry.Cancel(session!);
        await session!.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PowerShellSessionStateValue.Cancelled, session.State);

        Assert.Throws<ArgumentException>(() => Process.GetProcessById(childPid));
    }

    [PwshFact]
    public async Task Start_RealPwsh_CancelReturnsTerminalState()
    {
        var pwshPath = GetPwshPath();
        Assert.NotNull(pwshPath);

        var registry = BuildRegistry();
        var executor = new PowerShellSessionExecutor(registry, NullLogger<PowerShellSessionExecutor>());
        var session = registry.TryCreate("dev-1", 4096, out _);
        Assert.NotNull(session);

        executor.StartBackground(
            session!,
            pwshPath!,
            AppDomain.CurrentDomain.BaseDirectory,
            "Start-Sleep -Seconds 10",
            timeoutSeconds: 30);

        await Task.Delay(1000);

        var handler = BuildHandler(registry);
        var cancelCmd = new PowerShellCancelCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = session.SessionId
        };

        var result = await handler.HandleAsync(cancelCmd, CancellationToken.None);
        Assert.True(result.Success);

        var payload = result.Data.Deserialize<PowerShellSessionResult>(
            LocalMcp.BuildingBlocks.Serialization.JsonOptions.Default);
        Assert.Equal("cancelled", payload!.State);
    }

    [PwshFact]
    public async Task Start_RealPwsh_RegistryShutdown_CancelAll_Verified()
    {
        var pwshPath = GetPwshPath();
        Assert.NotNull(pwshPath);

        var registry = BuildRegistry();
        var executor = new PowerShellSessionExecutor(registry, NullLogger<PowerShellSessionExecutor>());
        var session = registry.TryCreate("dev-1", 4096, out _);
        Assert.NotNull(session);

        executor.StartBackground(
            session!,
            pwshPath!,
            AppDomain.CurrentDomain.BaseDirectory,
            "Start-Sleep -Seconds 10",
            timeoutSeconds: 30);

        await Task.Delay(1000);

        var sw = Stopwatch.StartNew();
        registry.Dispose();
        sw.Stop();

        Assert.Equal(PowerShellSessionStateValue.Cancelled, session.State);
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public void SessionState_EmojiSplitAcrossAppends_HandlesRunesCorrectly()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 1024);
        var bytes = System.Text.Encoding.UTF8.GetBytes("🧪"); // 4 bytes
        Assert.Equal(4, bytes.Length);

        // 1. Append 2 bytes
        session.AppendStdout(bytes.Take(2).ToArray(), 2);

        // Poll should return empty and offset should not advance
        var snap1 = session.ReadOutput(0, 0, 100);
        Assert.Empty(snap1.StdoutBytes);
        Assert.Equal(0, snap1.NextStdoutOffset);

        // 2. Append the remaining 2 bytes
        session.AppendStdout(bytes.Skip(2).ToArray(), 2);

        // Poll should return the complete emoji and next offset is 4
        var snap2 = session.ReadOutput(0, 0, 100);
        Assert.Equal("🧪", System.Text.Encoding.UTF8.GetString(snap2.StdoutBytes));
        Assert.Equal(4, snap2.NextStdoutOffset);
    }

    [Fact]
    public void SessionState_CapCutsEmojiFirstAppend_ContinuationIgnoredLater_IsValidUtf8()
    {
        // Combined cap of 3 bytes. We write "A" (1 byte) then "🧪" (4 bytes).
        // The emoji cannot fit in the remaining 2 bytes. It is truncated.
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 3);
        session.AppendStdout("A"u8.ToArray(), 1);

        var bytes = System.Text.Encoding.UTF8.GetBytes("🧪");
        session.AppendStdout(bytes, 4);

        // If we now append continuation bytes, they should be ignored because the session is truncated.
        session.AppendStdout(new byte[] { 0x8A }, 1);

        var snap = session.ReadOutput(0, 0, 100);
        Assert.True(snap.Truncated);
        // The output should be just "A" (valid UTF-8), not containing any trailing continuation/orphan bytes
        Assert.Equal("A", System.Text.Encoding.UTF8.GetString(snap.StdoutBytes));
    }

    [Fact]
    public void SessionState_VietnameseCharacterSplitAcrossAppends()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 1024);
        var bytes = System.Text.Encoding.UTF8.GetBytes("á"); // 2 bytes
        Assert.Equal(2, bytes.Length);

        // Append 1st byte
        session.AppendStdout(bytes.Take(1).ToArray(), 1);
        var snap1 = session.ReadOutput(0, 0, 100);
        Assert.Empty(snap1.StdoutBytes);

        // Append 2nd byte
        session.AppendStdout(bytes.Skip(1).ToArray(), 1);
        var snap2 = session.ReadOutput(0, 0, 100);
        Assert.Equal("á", System.Text.Encoding.UTF8.GetString(snap2.StdoutBytes));
    }

    [Fact]
    public async Task SessionState_ConcurrentPollDuringAppend()
    {
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 10240);
        var stdoutBytes = System.Text.Encoding.UTF8.GetBytes("a");

        var appendTask = Task.Run(async () =>
        {
            for (int i = 0; i < 500; i++)
            {
                session.AppendStdout(stdoutBytes, 1);
                await Task.Delay(1);
            }
        });

        var pollTask = Task.Run(async () =>
        {
            long offset = 0;
            while (offset < 500)
            {
                var snap = session.ReadOutput(offset, 0, 100);
                offset = snap.NextStdoutOffset;
                await Task.Delay(1);
            }
        });

        await Task.WhenAll(appendTask, pollTask);
    }

    [Fact]
    public void SessionState_CombinedCapacity_StrictlyBounded()
    {
        // Combined budget of 1024 bytes
        var session = new PowerShellSessionState(Guid.NewGuid(), "dev-1", 1024);

        // Grow stdout and stderr
        session.AppendStdout(new byte[600], 600);
        session.AppendStderr(new byte[200], 200);

        // Total allocated capacity should be <= 1024
        int allocated = session.AllocatedStdoutCapacity + session.AllocatedStderrCapacity;
        Assert.True(allocated <= 1024, $"Allocated capacity {allocated} exceeded maxOutputBytes budget 1024.");
    }

    [PwshFact]
    public async Task RegistryShutdown_ForceKillsProcessTree_Verified()
    {
        var pwshPath = GetPwshPath();
        Assert.NotNull(pwshPath);

        var registry = BuildRegistry();
        try
        {
            var executor = new PowerShellSessionExecutor(registry, NullLogger<PowerShellSessionExecutor>());
            var session = registry.TryCreate("dev-1", 4096, out _);
            Assert.NotNull(session);

            // Start a process tree: pwsh starts cmd, which starts timeout, and sleeps
            executor.StartBackground(
                session!,
                pwshPath!,
                AppDomain.CurrentDomain.BaseDirectory,
                "$p = Start-Process cmd.exe -ArgumentList '/c timeout 100' -PassThru -NoNewWindow; Write-Output \"CHILD_PID=$($p.Id)\"; Start-Sleep -Seconds 10",
                timeoutSeconds: 30);

            int childPid = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (childPid == 0 && !cts.IsCancellationRequested)
            {
                var snap = session.ReadOutput(0, 0, 4096);
                var text = System.Text.Encoding.UTF8.GetString(snap.StdoutBytes);
                if (text.Contains("CHILD_PID="))
                {
                    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var match = lines.FirstOrDefault(l => l.StartsWith("CHILD_PID="));
                    if (match != null)
                    {
                        childPid = int.Parse(match.Substring("CHILD_PID=".Length));
                    }
                }
                await Task.Delay(100);
            }

            Assert.True(childPid > 0, "Failed to get child PID");

            var parentProcess = session.Process;
            Assert.NotNull(parentProcess);
            int parentPid = parentProcess.Id;
            Assert.False(parentProcess.HasExited);

            // Trigger CancelAll to force-kill remaining process tree
            registry.CancelAll();

            // Verify parent has exited via PID lookup
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(parentPid));

            // Verify child has exited via PID lookup
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(childPid));
        }
        finally
        {
            registry.Dispose();
        }
    }

    private class MockPowerShellSessionExecutor : PowerShellSessionExecutor
    {
        private readonly Action _onStart;
        public MockPowerShellSessionExecutor(
            PowerShellSessionRegistry registry,
            ILogger<PowerShellSessionExecutor> logger,
            Action onStart)
            : base(registry, logger)
        {
            _onStart = onStart;
        }

        public override void StartBackground(
            PowerShellSessionState session,
            string executable,
            string workingDirectory,
            string script,
            int timeoutSeconds)
        {
            _onStart();
        }
    }

    private class NonCancellableSessionState : PowerShellSessionState
    {
        public NonCancellableSessionState(Guid sessionId, string deviceId, int maxOutputBytes)
            : base(sessionId, deviceId, maxOutputBytes)
        {
        }

        public override void SignalCancel()
        {
            SignalCancelCalled = true;
        }

        public bool SignalCancelCalled { get; private set; }
    }

    private class TestNonCancellableRegistry : PowerShellSessionRegistry
    {
        public TestNonCancellableRegistry(ILogger<PowerShellSessionRegistry> logger) : base(logger)
        {
        }

        internal override PowerShellSessionState CreateSessionState(string deviceId, int maxOutputBytes)
        {
            return new NonCancellableSessionState(Guid.NewGuid(), deviceId, maxOutputBytes);
        }
    }

    [Fact]
    public void RegistryShutdown_FallbackForceKillBranch_Verified()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<PowerShellSessionRegistry>.Instance;
        var registry = new TestNonCancellableRegistry(logger);
        
        string? error;
        var session = registry.TryCreate("dev-1", 1024, out error);
        Assert.NotNull(session);
        var nonCancellableSession = (NonCancellableSessionState)session!;

        // Start a real process tree that ignores cancellation
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            Arguments = "-NoProfile -Command \"Start-Process cmd -ArgumentList '/c timeout /t 60 /nobreak' -NoNewWindow; Start-Sleep -Seconds 60\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = Process.Start(startInfo);
        Assert.NotNull(proc);
        session.Process = proc;

        // Measure time
        var sw = Stopwatch.StartNew();
        
        // This will cancel all, wait 5s, fail graceful, force kill
        registry.CancelAll();
        
        sw.Stop();

        // Verify force kill was initiated
        Assert.True(nonCancellableSession.SignalCancelCalled);
        
        // Verify process tree was terminated
        Assert.True(proc.HasExited);
        
        // Verify time elapsed was at least 4.9 seconds (due to 5s graceful wait)
        Assert.True(sw.Elapsed.TotalSeconds >= 4.9, $"Should wait at least 4.9s, actually waited {sw.Elapsed.TotalSeconds}s");
        
        registry.Dispose();
    }
}
