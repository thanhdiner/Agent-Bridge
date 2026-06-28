using LocalMcp.Agent.Windows.AppLaunch;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using NSubstitute;

namespace LocalMcp.UnitTests;

public sealed class AppCloserTests
{
    [Fact]
    public async Task CloseAsync_ByProcessId_RequestsGracefulClose()
    {
        var catalog = Substitute.For<IAppProcessCatalog>();
        var process = Process(42, "notepad");
        catalog.CurrentProcessId.Returns(999);
        catalog.GetById(42).Returns(process);
        process.CloseMainWindow().Returns(true);
        process.WaitForExitAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        var closer = new AppCloser(catalog);

        var result = await closer.CloseAsync(
            42,
            null,
            allMatches: false,
            force: false,
            entireProcessTree: false,
            timeoutMs: 5_000,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(1, result.Data!.MatchedCount);
        Assert.Equal(1, result.Data.ClosedCount);
        Assert.True(result.Data.Processes[0].GracefulCloseRequested);
        process.Received(1).CloseMainWindow();
        process.DidNotReceive().Kill(Arg.Any<bool>());
        process.Received(1).Dispose();
    }

    [Fact]
    public async Task CloseAsync_ForceFallback_KillsRequestedProcessTree()
    {
        var catalog = Substitute.For<IAppProcessCatalog>();
        var process = Process(42, "example");
        catalog.CurrentProcessId.Returns(999);
        catalog.GetById(42).Returns(process);
        process.CloseMainWindow().Returns(false);
        process.WaitForExitAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        var closer = new AppCloser(catalog);

        var result = await closer.CloseAsync(
            42,
            "example.exe",
            allMatches: false,
            force: true,
            entireProcessTree: true,
            timeoutMs: 5_000,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Processes[0].ForceKillRequested);
        Assert.True(result.Data.Processes[0].Closed);
        process.Received(1).Kill(true);
    }

    [Fact]
    public async Task CloseAsync_ProcessNameMatchesMultiple_RequiresAllMatches()
    {
        var catalog = Substitute.For<IAppProcessCatalog>();
        var first = Process(11, "chrome");
        var second = Process(22, "chrome");
        catalog.GetByName("chrome").Returns(new IAppProcess[] { first, second });
        var closer = new AppCloser(catalog);

        var result = await closer.CloseAsync(
            null,
            "chrome.exe",
            allMatches: false,
            force: false,
            entireProcessTree: false,
            timeoutMs: 5_000,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.AppProcessAmbiguous, result.Error?.Code);
        first.Received(1).Dispose();
        second.Received(1).Dispose();
        first.DidNotReceive().CloseMainWindow();
        second.DidNotReceive().CloseMainWindow();
    }

    [Fact]
    public async Task CloseAsync_ProcessIdAndNameMismatch_RejectsPidReuse()
    {
        var catalog = Substitute.For<IAppProcessCatalog>();
        var process = Process(42, "other");
        catalog.GetById(42).Returns(process);
        var closer = new AppCloser(catalog);

        var result = await closer.CloseAsync(
            42,
            "notepad",
            allMatches: false,
            force: true,
            entireProcessTree: true,
            timeoutMs: 5_000,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.AppProcessMismatch, result.Error?.Code);
        process.DidNotReceive().Kill(Arg.Any<bool>());
        process.Received(1).Dispose();
    }

    [Fact]
    public async Task CloseAsync_CurrentAgentProcess_IsProtected()
    {
        var catalog = Substitute.For<IAppProcessCatalog>();
        var process = Process(42, "LocalMcp.Agent.Windows");
        catalog.CurrentProcessId.Returns(42);
        catalog.GetById(42).Returns(process);
        var closer = new AppCloser(catalog);

        var result = await closer.CloseAsync(
            42,
            null,
            allMatches: false,
            force: true,
            entireProcessTree: true,
            timeoutMs: 5_000,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.Data!.ClosedCount);
        Assert.Equal(ErrorCodes.AppCloseProtected, result.Data.Processes[0].ErrorCode);
        process.DidNotReceive().CloseMainWindow();
        process.DidNotReceive().Kill(Arg.Any<bool>());
    }

    [Fact]
    public void AppCloseCommandTimeout_IncludesGatewayBuffer()
    {
        var command = new AppCloseCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessName = "notepad",
            TimeoutMs = 25_000
        };

        Assert.Equal(TimeSpan.FromSeconds(35), AgentCommandTimeouts.GetTimeout(command));
    }

    private static IAppProcess Process(int id, string name)
    {
        var process = Substitute.For<IAppProcess>();
        process.Id.Returns(id);
        process.Name.Returns(name);
        process.HasExited.Returns(false);
        return process;
    }
}
