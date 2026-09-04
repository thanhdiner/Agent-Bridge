using System.Buffers.Binary;
using System.Text;
using LocalMcp.Agent.AndroidAdb;
using LocalMcp.Contracts.Commands;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalMcp.UnitTests;

public sealed class AndroidCommandHandlerTests
{
    [Fact]
    public async Task Screenshot_ReturnsValidatedPngMetadataWithoutWritingAFile()
    {
        var png = BuildPngHeader(1080, 2400);
        var adb = new FakeAdbExecutor((arguments, _) =>
        {
            Assert.Equal(["exec-out", "screencap", "-p"], arguments);
            return new AdbExecutionResult(0, png, string.Empty, false);
        });
        var handler = CreateHandler(adb);

        var result = await handler.HandleAsync(new AndroidScreenshotCommand
        {
            CommandId = Guid.NewGuid(), DeviceId = "android-phone", CreatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1080, result.Data.GetProperty("width").GetInt32());
        Assert.Equal(2400, result.Data.GetProperty("height").GetInt32());
        Assert.Equal(Convert.ToBase64String(png), result.Data.GetProperty("pngBase64").GetString());
    }

    [Fact]
    public async Task Tap_UsesArgumentListAndConfiguredSerialExecutor()
    {
        IReadOnlyList<string>? received = null;
        var adb = new FakeAdbExecutor((arguments, _) =>
        {
            received = arguments;
            return new AdbExecutionResult(0, [], string.Empty, false);
        });
        var handler = CreateHandler(adb);
        var command = new AndroidTapCommand
        {
            CommandId = Guid.NewGuid(), DeviceId = "android-phone", CreatedAt = DateTimeOffset.UtcNow,
            X = 123, Y = 456
        };

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["shell", "input", "tap", "123", "456"], received);
    }

    [Theory]
    [InlineData("simple text", true)]
    [InlineData("user@example.com/path", true)]
    [InlineData("hello&whoami", false)]
    [InlineData("xin chào", false)]
    public void SafeTextValidation_BlocksShellMetacharactersAndUnsupportedUnicode(string text, bool expected)
    {
        Assert.Equal(expected, AndroidCommandHandler.IsSafeAdbText(text));
    }

    [Fact]
    public void DeviceId_IsStableAndSafeForWirelessSerial()
    {
        Assert.Equal("android-192.168.1.50-41277", AndroidCommandHandler.BuildDeviceId("192.168.1.50:41277"));
    }

    private static AndroidCommandHandler CreateHandler(IAdbExecutor adb) => new(
        adb,
        Options.Create(new AndroidAdbOptions
        {
            Serial = "192.168.1.50:41277",
            MaxScreenshotBytes = 1024 * 1024
        }),
        NullLogger<AndroidCommandHandler>.Instance);

    private static byte[] BuildPngHeader(int width, int height)
    {
        var bytes = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private sealed class FakeAdbExecutor : IAdbExecutor
    {
        private readonly Func<IReadOnlyList<string>, int, AdbExecutionResult> _execute;

        public FakeAdbExecutor(Func<IReadOnlyList<string>, int, AdbExecutionResult> execute) => _execute = execute;

        public Task<AdbExecutionResult> ExecuteAsync(
            IReadOnlyList<string> arguments,
            int maxOutputBytes = 1024 * 1024,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_execute(arguments, maxOutputBytes));
    }
}
