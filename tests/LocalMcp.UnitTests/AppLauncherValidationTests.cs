using LocalMcp.Agent.Windows.AppLaunch;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Commands;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LocalMcp.UnitTests;

public sealed class AppLauncherValidationTests
{
    [Fact]
    public async Task LaunchAsync_EmptyExecutable_ReturnsInvalidRequest()
    {
        var result = await CreateLauncher().LaunchAsync(
            string.Empty, [], null, false, null, 1000, 100,
            Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task LaunchAsync_TooManyArguments_ReturnsInvalidRequest()
    {
        var arguments = Enumerable.Repeat("x", 65).ToArray();
        var result = await CreateLauncher().LaunchAsync(
            "notepad.exe", arguments, null, false, null, 1000, 100,
            Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(300001, 100)]
    [InlineData(1000, 24)]
    [InlineData(1000, 5001)]
    public async Task LaunchAsync_InvalidTiming_ReturnsInvalidRequest(int timeoutMs, int pollIntervalMs)
    {
        var result = await CreateLauncher().LaunchAsync(
            "notepad.exe", [], null, true, null, timeoutMs, pollIntervalMs,
            Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("pythonw.exe")]
    [InlineData("node.exe")]
    [InlineData("mshta.exe")]
    public void BlockedExecutableNames_AreRejected(string name) =>
        Assert.True(AppLauncher.IsBlockedExecutableName(name));

    [Fact]
    public void BlockedExecutableIdentity_RejectsRenamedSystemHost()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var source = Path.Combine(systemDirectory, "wscript.exe");
        if (!File.Exists(source))
            return;

        var renamed = Path.Combine(Path.GetTempPath(), $"renamed-host-{Guid.NewGuid():N}.exe");
        File.Copy(source, renamed);
        try
        {
            Assert.True(AppLauncher.IsBlockedExecutable(renamed));
        }
        finally
        {
            File.Delete(renamed);
        }
    }

    [Fact]
    public void ResolveExecutable_NonAllowlistedBareName_IsRejected()
    {
        var launcher = CreateLauncher();
        var error = launcher.ResolveExecutable("unknown-app.exe", out var resolved);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.AppExecutableNotAllowed, error!.Code);
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void CreateStartInfo_UsesArgumentListAndScrubsSensitiveEnvironment()
    {
        const string secretName = "LOCALMCP_APP_LAUNCH_TEST_TOKEN";
        Environment.SetEnvironmentVariable(secretName, "secret");
        try
        {
            var launcher = CreateLauncher();
            var info = launcher.CreateStartInfo(
                "C:\\Apps\\Example.exe",
                "C:\\Apps",
                ["one", "two words", "a&b"]);

            Assert.False(info.UseShellExecute);
            Assert.False(info.CreateNoWindow);
            Assert.Equal(new[] { "one", "two words", "a&b" }, info.ArgumentList);
            Assert.False(info.Environment.ContainsKey(secretName));
            Assert.Equal("RunAsInvoker", info.Environment["__COMPAT_LAYER"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, null);
        }
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void IsWindowsGuiExecutable_ReadsPeSubsystem(ushort subsystem, bool expected)
    {
        var path = CreateFakePeFile(subsystem);
        try
        {
            Assert.Equal(expected, AppLauncher.IsWindowsGuiExecutable(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CommandTimeout_WaitingLaunchAddsBuffer()
    {
        var command = new AppLaunchCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            Executable = "notepad.exe",
            WaitForWindow = true,
            TimeoutMs = 25_000
        };

        Assert.Equal(TimeSpan.FromSeconds(35), AgentCommandTimeouts.GetTimeout(command));
    }

    [Fact]
    public void CommandTimeout_NoWindowWaitUsesStartupTimeout()
    {
        var command = new AppLaunchCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            Executable = "notepad.exe",
            WaitForWindow = false
        };

        Assert.Equal(TimeSpan.FromSeconds(15), AgentCommandTimeouts.GetTimeout(command));
    }

    private static AppLauncher CreateLauncher(AppLaunchOptions? options = null) =>
        new(
            Substitute.For<IPathPolicy>(),
            Substitute.For<IUiAutomationExecutor>(),
            Options.Create(options ?? new AppLaunchOptions()),
            NullLogger<AppLauncher>.Instance);

    private static string CreateFakePeFile(ushort subsystem)
    {
        var path = Path.Combine(Path.GetTempPath(), $"localmcp-pe-{Guid.NewGuid():N}.exe");
        var bytes = new byte[512];
        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream);
        stream.Position = 0x3C;
        writer.Write(0x80);
        stream.Position = 0x80;
        writer.Write(0x00004550u);
        stream.Position = 0x80 + 24;
        writer.Write((ushort)0x20B);
        stream.Position = 0x80 + 24 + 68;
        writer.Write(subsystem);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
