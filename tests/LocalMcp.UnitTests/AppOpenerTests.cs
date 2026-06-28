using LocalMcp.Agent.Windows.AppLaunch;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using NSubstitute;

namespace LocalMcp.UnitTests;

public sealed class AppOpenerTests
{
    [Fact]
    public async Task OpenAsync_ResolvedApplicationWithArguments_LaunchesTrustedPath()
    {
        var resolver = Substitute.For<IAppResolver>();
        var launcher = Substitute.For<IAppLauncher>();
        var uiAutomation = Substitute.For<IUiAutomationExecutor>();
        var commandId = Guid.NewGuid();
        var executablePath = @"C:\Program Files\Example\Example.exe";

        resolver.ResolveAsync("example", false, commandId, Arg.Any<CancellationToken>())
            .Returns(Resolved(commandId, "example", executablePath, cacheHit: true));
        launcher.LaunchResolvedAsync(
                executablePath,
                Arg.Any<IReadOnlyList<string>>(),
                true,
                null,
                15_000,
                100,
                commandId,
                Arg.Any<CancellationToken>())
            .Returns(Launched(commandId, executablePath, "Example", 42));

        var opener = new AppOpener(resolver, launcher, uiAutomation);
        var result = await opener.OpenAsync(
            "example",
            ["--safe"],
            refresh: false,
            focusIfRunning: true,
            waitForWindow: true,
            windowTitleContains: null,
            timeoutMs: 15_000,
            pollIntervalMs: 100,
            commandId,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("launched", result.Data!.Action);
        Assert.False(result.Data.FocusedExisting);
        Assert.Equal(executablePath, result.Data.ExecutablePath);
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
        await uiAutomation.DidNotReceiveWithAnyArgs().ListWindowsAsync(
            default,
            default,
            default,
            default,
            default);
    }

    [Fact]
    public async Task OpenAsync_RunningApplicationWithoutArguments_FocusesExistingWindow()
    {
        var resolver = Substitute.For<IAppResolver>();
        var launcher = Substitute.For<IAppLauncher>();
        var uiAutomation = Substitute.For<IUiAutomationExecutor>();
        var commandId = Guid.NewGuid();
        var executablePath = @"C:\Program Files\Example\Example.exe";
        var window = CreateWindow("0x1234", "Example document", 77, "Example", isForeground: false);

        resolver.ResolveAsync("example", false, commandId, Arg.Any<CancellationToken>())
            .Returns(Resolved(commandId, "example", executablePath, cacheHit: false));
        uiAutomation.ListWindowsAsync(false, true, 500, commandId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult<WindowListResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new WindowListResult
                {
                    Windows = [window],
                    Count = 1,
                    MaxWindows = 500,
                    Truncated = false
                }
            }));
        uiAutomation.FocusWindowAsync(window.WindowHandle, commandId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult<WindowFocusResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new WindowFocusResult
                {
                    WindowHandle = window.WindowHandle,
                    PreviousForegroundWindow = "0x0",
                    IsForeground = true
                }
            }));

        var opener = new AppOpener(resolver, launcher, uiAutomation);
        var result = await opener.OpenAsync(
            "example",
            [],
            refresh: false,
            focusIfRunning: true,
            waitForWindow: true,
            windowTitleContains: null,
            timeoutMs: 15_000,
            pollIntervalMs: 100,
            commandId,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("focused-existing", result.Data!.Action);
        Assert.True(result.Data.FocusedExisting);
        Assert.False(result.Data.Launch.Started);
        Assert.Equal(77, result.Data.Launch.ProcessId);
        await launcher.DidNotReceiveWithAnyArgs().LaunchResolvedAsync(
            default!,
            default!,
            default,
            default,
            default,
            default,
            default,
            default);
    }

    [Fact]
    public async Task OpenAsync_YouTubeAlias_AddsUrlAndDefaultWindowTitleMatcher()
    {
        var resolver = Substitute.For<IAppResolver>();
        var launcher = Substitute.For<IAppLauncher>();
        var uiAutomation = Substitute.For<IUiAutomationExecutor>();
        var commandId = Guid.NewGuid();
        var executablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

        resolver.ResolveAsync("chrome", false, commandId, Arg.Any<CancellationToken>())
            .Returns(Resolved(commandId, "chrome", executablePath, cacheHit: true));
        launcher.LaunchResolvedAsync(
                executablePath,
                Arg.Any<IReadOnlyList<string>>(),
                true,
                "YouTube",
                15_000,
                100,
                commandId,
                Arg.Any<CancellationToken>())
            .Returns(Launched(commandId, executablePath, "chrome", 88));

        var opener = new AppOpener(resolver, launcher, uiAutomation);
        var result = await opener.OpenAsync(
            "youtube",
            [],
            refresh: false,
            focusIfRunning: true,
            waitForWindow: true,
            windowTitleContains: null,
            timeoutMs: 15_000,
            pollIntervalMs: 100,
            commandId,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.AliasApplied);
        Assert.Equal("chrome", result.Data.ResolvedAppId);
        Assert.Equal("launched", result.Data.Action);
        await launcher.Received(1).LaunchResolvedAsync(
            executablePath,
            Arg.Is<IReadOnlyList<string>>(items => items.SequenceEqual(new[] { "https://www.youtube.com" })),
            true,
            "YouTube",
            15_000,
            100,
            commandId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenAsync_YouTubeAlias_ExplicitWindowTitleMatcherOverridesDefault()
    {
        var resolver = Substitute.For<IAppResolver>();
        var launcher = Substitute.For<IAppLauncher>();
        var uiAutomation = Substitute.For<IUiAutomationExecutor>();
        var commandId = Guid.NewGuid();
        var executablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

        resolver.ResolveAsync("chrome", false, commandId, Arg.Any<CancellationToken>())
            .Returns(Resolved(commandId, "chrome", executablePath, cacheHit: true));
        launcher.LaunchResolvedAsync(
                executablePath,
                Arg.Any<IReadOnlyList<string>>(),
                true,
                "YouTube Music",
                15_000,
                100,
                commandId,
                Arg.Any<CancellationToken>())
            .Returns(Launched(commandId, executablePath, "chrome", 89));

        var opener = new AppOpener(resolver, launcher, uiAutomation);
        var result = await opener.OpenAsync(
            "youtube",
            [],
            refresh: false,
            focusIfRunning: true,
            waitForWindow: true,
            windowTitleContains: "YouTube Music",
            timeoutMs: 15_000,
            pollIntervalMs: 100,
            commandId,
            CancellationToken.None);

        Assert.True(result.Success);
        await launcher.Received(1).LaunchResolvedAsync(
            executablePath,
            Arg.Is<IReadOnlyList<string>>(items => items.SequenceEqual(new[] { "https://www.youtube.com" })),
            true,
            "YouTube Music",
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
        var uiAutomation = Substitute.For<IUiAutomationExecutor>();
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

        var opener = new AppOpener(resolver, launcher, uiAutomation);
        var result = await opener.OpenAsync(
            "missing",
            [],
            refresh: false,
            focusIfRunning: true,
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

    private static Task<CommandResult<AppResolveResult>> Resolved(
        Guid commandId,
        string appId,
        string executablePath,
        bool cacheHit) =>
        Task.FromResult(new CommandResult<AppResolveResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new AppResolveResult
            {
                AppId = appId,
                NormalizedAppId = appId,
                Resolved = true,
                ExecutablePath = executablePath,
                ProcessName = Path.GetFileNameWithoutExtension(executablePath),
                Source = "app-paths",
                CacheHit = cacheHit,
                ElapsedMs = 2
            }
        });

    private static Task<CommandResult<AppLaunchResult>> Launched(
        Guid commandId,
        string executablePath,
        string processName,
        int processId) =>
        Task.FromResult(new CommandResult<AppLaunchResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new AppLaunchResult
            {
                ExecutablePath = executablePath,
                ProcessName = processName,
                ProcessId = processId,
                Started = true,
                StartedAt = DateTimeOffset.UtcNow,
                WaitForWindow = true
            }
        });

    private static WindowInfo CreateWindow(
        string handle,
        string title,
        int processId,
        string processName,
        bool isForeground) =>
        new()
        {
            WindowHandle = handle,
            WindowHandleDecimal = "4660",
            Title = title,
            ProcessId = processId,
            ProcessName = processName,
            ClassName = "ExampleWindow",
            Bounds = new UiBounds { X = 0, Y = 0, Width = 800, Height = 600 },
            IsVisible = true,
            IsEnabled = true,
            IsForeground = isForeground,
            ZOrder = 1
        };
}
