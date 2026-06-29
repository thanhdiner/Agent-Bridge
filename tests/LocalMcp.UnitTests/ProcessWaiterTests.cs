using LocalMcp.Agent.Windows.AppLaunch;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using NSubstitute;

namespace LocalMcp.UnitTests;

public sealed class ProcessWaiterTests
{
    [Theory]
    [InlineData("exists", "exists")]
    [InlineData(" appears ", "exists")]
    [InlineData("not-exists", "not-exists")]
    [InlineData("disappears", "not-exists")]
    [InlineData("exited", "not-exists")]
    public void Conditions_NormalizeAliases(string input, string expected)
    {
        Assert.True(ProcessWaitConditions.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public async Task WaitAsync_ByProcessId_ReturnsExistingProcess()
    {
        var catalog = Substitute.For<IAppProcessCatalog>();
        var process = Process(42, "notepad", hasExited: false);
        catalog.GetById(42).Returns(process);
        var waiter = new ProcessWaiter(catalog);

        var result = await waiter.WaitAsync(
            processId: 42,
            processName: "notepad.exe",
            occurrenceIndex: 0,
            condition: ProcessWaitConditions.Exists,
            timeoutMs: 1_000,
            pollIntervalMs: 25,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.Equal("condition-satisfied", result.Data!.CompletionReason);
        Assert.Equal("exists", result.Data.FinalState);
        Assert.Equal(42, result.Data.ProcessId);
        Assert.Equal("notepad", result.Data.ProcessName);
        Assert.True(result.Data.ProcessFound);
        process.Received(1).Dispose();
    }

    [Fact]
    public async Task WaitAsync_Disappears_PollsUntilProcessIsMissing()
    {
        var catalog = Substitute.For<IAppProcessCatalog>();
        var process = Process(42, "example", hasExited: false);
        catalog.GetById(42).Returns(process, (IAppProcess?)null);
        var waiter = new ProcessWaiter(catalog);

        var result = await waiter.WaitAsync(
            processId: 42,
            processName: null,
            occurrenceIndex: 0,
            condition: "disappears",
            timeoutMs: 1_000,
            pollIntervalMs: 25,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(ProcessWaitConditions.NotExists, result.Data!.Condition);
        Assert.Equal("not-exists", result.Data.FinalState);
        Assert.False(result.Data.ProcessFound);
        Assert.True(result.Data.PollCount >= 2);
    }

    [Fact]
    public async Task WaitAsync_ByName_UsesOccurrenceIndexAmongLiveProcesses()
    {
        var catalog = Substitute.For<IAppProcessCatalog>();
        var first = Process(11, "chrome", hasExited: false);
        var second = Process(22, "chrome", hasExited: false);
        catalog.GetByName("chrome").Returns(new IAppProcess[] { first, second });
        var waiter = new ProcessWaiter(catalog);

        var result = await waiter.WaitAsync(
            processId: null,
            processName: "chrome.exe",
            occurrenceIndex: 1,
            condition: ProcessWaitConditions.Exists,
            timeoutMs: 1_000,
            pollIntervalMs: 25,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(22, result.Data!.ProcessId);
        first.Received(1).Dispose();
        second.Received(1).Dispose();
    }

    [Fact]
    public async Task WaitAsync_MissingSelector_ReturnsInvalidRequest()
    {
        var waiter = new ProcessWaiter(Substitute.For<IAppProcessCatalog>());

        var result = await waiter.WaitAsync(
            null,
            null,
            0,
            ProcessWaitConditions.Exists,
            1_000,
            100,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task WaitAsync_Timeout_ReturnsProcessWaitTimeout()
    {
        var catalog = Substitute.For<IAppProcessCatalog>();
        catalog.GetById(42).Returns((IAppProcess?)null);
        var waiter = new ProcessWaiter(catalog);

        var result = await waiter.WaitAsync(
            42,
            null,
            0,
            ProcessWaitConditions.Exists,
            75,
            25,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ProcessWaitTimeout, result.Error?.Code);
        Assert.Contains("final poll", result.Error?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WaitAsync_Cancelled_ReturnsCommandCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var waiter = new ProcessWaiter(Substitute.For<IAppProcessCatalog>());

        var result = await waiter.WaitAsync(
            42,
            null,
            0,
            ProcessWaitConditions.Exists,
            1_000,
            25,
            Guid.NewGuid(),
            cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CommandCancelled, result.Error?.Code);
    }

    [Fact]
    public async Task WaitAsync_ExeOnlyProcessName_ReturnsInvalidRequest()
    {
        var waiter = new ProcessWaiter(Substitute.For<IAppProcessCatalog>());

        var result = await waiter.WaitAsync(
            null,
            ".exe",
            0,
            ProcessWaitConditions.Exists,
            1_000,
            25,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(300001, 100)]
    [InlineData(1000, 24)]
    [InlineData(1000, 5001)]
    public async Task WaitAsync_InvalidTiming_ReturnsInvalidRequest(int timeoutMs, int pollIntervalMs)
    {
        var waiter = new ProcessWaiter(Substitute.For<IAppProcessCatalog>());

        var result = await waiter.WaitAsync(
            42,
            null,
            0,
            ProcessWaitConditions.Exists,
            timeoutMs,
            pollIntervalMs,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public void AgentCommandTimeouts_ProcessWaitAddsTransportBuffer()
    {
        var command = new ProcessWaitCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessName = "notepad",
            TimeoutMs = 25_000
        };

        Assert.Equal(TimeSpan.FromSeconds(35), AgentCommandTimeouts.GetTimeout(command));
    }

    private static IAppProcess Process(int id, string name, bool hasExited)
    {
        var process = Substitute.For<IAppProcess>();
        process.Id.Returns(id);
        process.Name.Returns(name);
        process.HasExited.Returns(hasExited);
        return process;
    }
}
