using LocalMcp.Agent.Windows.AppLaunch;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using NSubstitute;

namespace LocalMcp.UnitTests;

public sealed class AppOpenerTests
{
    [Fact]
    public async Task OpenAsync_ResolvedApplication_LaunchesTrustedPath()
    {
        var resolver = Substitute.For<IAppResolver>();
        var launcher = Substitute.For<IAppLauncher>();
        var commandId = Guid.NewGuid();
        var executablePath = @"C:\Program Files\Example\Example.exe";

        resolver.ResolveAsync("example", false, commandId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult<AppResolveResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new AppResolveResult
                {
                    AppId = "example",
                    NormalizedAppId = "example",
                    Resolved = true,
                    ExecutablePath = executablePath,
                    ProcessName = "Example",
                    Source = "app-paths",
                    CacheHit = true,
                    ElapsedMs = 2
                }
            }));

        launcher.LaunchResolvedAsync(
                executablePath,
                Arg.Any<IReadOnlyList<string>>(),
                true,
                null,
                15_000,
                100,
                commandId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult<AppLaunchResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new AppLaunchResult
                {
                    ExecutablePath = executablePath,
                    ProcessName = "Example",
                    ProcessId = 42,
                    Started = true,
                    StartedAt = DateTimeOffset.UtcNow,
                    WaitForWindow = true
                }
            }));

        var opener = new AppOpener(resolver, launcher);
        var result = await opener.OpenAsync(
            "example",
            ["--safe"],
            refresh: false,
            waitForWindow: true,
            windowTitleContains: null,
            timeoutMs: 15_000,
            pollIntervalMs: 100,
            commandId,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(executablePath, result.Data!.ExecutablePath);
        Assert.True(result.Data.CacheHit);
        Assert.Equal(42, result.Data.Launch.ProcessId);
        await launcher.Received(1).LaunchResolvedAsync(
            executablePath,
            Arg.Is<IReadOnlyList<string>>(items => items.SequenceEqual(new[] { "--safe" })),
            true,
            null,
            15_000,
            100,
            commandId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenAsync_UnresolvedApplication_ReturnsAppNotFound()
    {
        var resolver = Substitute.For<IAppResolver>();
        var launcher = Substitute.For<IAppLauncher>();
        var commandId = Guid.NewGuid();

        resolver.ResolveAsync("missing", false, commandId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult<AppResolveResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new AppResolveResult
                {
                    AppId = "missing",
                    NormalizedAppId = "missing",
                    Resolved = false,
                    CacheHit = false,
                    ElapsedMs = 5
                }
            }));

        var opener = new AppOpener(resolver, launcher);
        var result = await opener.OpenAsync(
            "missing",
            [],
            refresh: false,
            waitForWindow: false,
            windowTitleContains: null,
            timeoutMs: 15_000,
            pollIntervalMs: 100,
            commandId,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.AppNotFound, result.Error?.Code);
    }

    [Fact]
    public void AppOpenCommandTimeout_IncludesResolutionBuffer()
    {
        var command = new AppOpenCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            AppId = "chrome",
            WaitForWindow = true,
            TimeoutMs = 25_000
        };

        Assert.Equal(TimeSpan.FromSeconds(40), AgentCommandTimeouts.GetTimeout(command));
    }
}
