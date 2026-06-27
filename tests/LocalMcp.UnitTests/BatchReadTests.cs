using System.Reflection;
using System.Text.Json;
using LocalMcp.Agent.Windows.Commands;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalMcp.UnitTests;

public sealed class BatchReadTests : IDisposable
{
    private readonly string _root;

    public BatchReadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "LocalMcp_BatchRead_" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private CommandHandler CreateHandler()
    {
        var options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _root },
            WritableRoots = new List<string> { _root },
            DeniedSegments = new List<string> { ".git" },
            DeniedFileNames = new List<string>(),
            DeniedWriteFileNames = new List<string>(),
            DeniedWriteExtensions = new List<string>(),
            MaxReadBytes = 2_097_152,
            MaxWriteBytes = 524_288
        };
        var policy = new PathPolicy(Options.Create(options));
        var executor = new FileSystemExecutor(
            policy,
            Options.Create(options),
            NullLogger<FileSystemExecutor>.Instance);
        return new CommandHandler(
            policy,
            executor,
            NullLogger<CommandHandler>.Instance);
    }

    private static BatchReadCommand CreateCommand(
        IEnumerable<string> paths,
        int perFile = 262_144,
        long total = 2_097_152) => new()
    {
        CommandId = Guid.NewGuid(),
        DeviceId = "test-device",
        CreatedAt = DateTimeOffset.UtcNow,
        Paths = paths.ToList(),
        MaxBytesPerFile = perFile,
        MaxTotalBytes = total
    };

    private static BatchReadResult GetData(CommandResult<JsonElement> result)
    {
        Assert.True(result.Success);
        return Assert.IsType<JsonElement>(result.Data)
            .Deserialize<BatchReadResult>(JsonOptions.Default)!;
    }

    [Fact]
    public async Task ReturnsFilesInInputOrder()
    {
        var first = Path.Combine(_root, "first.txt");
        var second = Path.Combine(_root, "second.txt");
        await File.WriteAllTextAsync(first, "alpha");
        await File.WriteAllTextAsync(second, "beta");

        var data = GetData(await CreateHandler().HandleAsync(
            CreateCommand(new[] { second, first }),
            CancellationToken.None));

        Assert.Equal(2, data.Succeeded);
        Assert.Equal(0, data.Failed);
        Assert.Equal(9, data.TotalBytesReturned);
        Assert.Equal(new[] { second, first }, data.Items.Select(item => item.Path));
        Assert.Equal("beta", data.Items[0].Data!.Content);
        Assert.Equal("alpha", data.Items[1].Data!.Content);
    }

    [Fact]
    public async Task AppliesUtf8SafePerFileAndTotalLimits()
    {
        var first = Path.Combine(_root, "unicode.txt");
        var second = Path.Combine(_root, "second.txt");
        await File.WriteAllTextAsync(first, "a😀b");
        await File.WriteAllTextAsync(second, "wxyz");

        var data = GetData(await CreateHandler().HandleAsync(
            CreateCommand(new[] { first, second }, perFile: 5, total: 7),
            CancellationToken.None));

        Assert.Equal("a😀", data.Items[0].Data!.Content);
        Assert.Equal(5, data.Items[0].Data!.BytesReturned);
        Assert.True(data.Items[0].Data!.Truncated);
        Assert.Equal("wx", data.Items[1].Data!.Content);
        Assert.Equal(2, data.Items[1].Data!.BytesReturned);
        Assert.True(data.Items[1].Data!.Truncated);
        Assert.Equal(7, data.TotalBytesReturned);
    }

    [Fact]
    public async Task ItemFailuresDoNotAbortBatch()
    {
        var valid = Path.Combine(_root, "valid.txt");
        var missing = Path.Combine(_root, "missing.txt");
        var deniedDirectory = Path.Combine(_root, ".git");
        var denied = Path.Combine(deniedDirectory, "blocked.txt");
        await File.WriteAllTextAsync(valid, "ok");
        Directory.CreateDirectory(deniedDirectory);
        await File.WriteAllTextAsync(denied, "blocked");

        var data = GetData(await CreateHandler().HandleAsync(
            CreateCommand(new[] { missing, valid, denied }),
            CancellationToken.None));

        Assert.Equal(1, data.Succeeded);
        Assert.Equal(2, data.Failed);
        Assert.Equal(ErrorCodes.FileNotFound, data.Items[0].Error!.Code);
        Assert.Equal("ok", data.Items[1].Data!.Content);
        Assert.Equal(ErrorCodes.AccessDenied, data.Items[2].Error!.Code);
    }

    [Fact]
    public async Task BinaryFileIsAnItemFailure()
    {
        var text = Path.Combine(_root, "text.txt");
        var binary = Path.Combine(_root, "binary.dat");
        await File.WriteAllTextAsync(text, "ok");
        await File.WriteAllBytesAsync(binary, new byte[] { 1, 0, 2 });

        var data = GetData(await CreateHandler().HandleAsync(
            CreateCommand(new[] { text, binary }),
            CancellationToken.None));

        Assert.True(data.Items[0].Success);
        Assert.False(data.Items[1].Success);
        Assert.Equal(ErrorCodes.BinaryFileNotSupported, data.Items[1].Error!.Code);
    }

    [Theory]
    [InlineData(0, 262144, 2097152)]
    [InlineData(21, 262144, 2097152)]
    [InlineData(1, 0, 2097152)]
    [InlineData(1, 1048577, 2097152)]
    [InlineData(1, 262144, 0)]
    [InlineData(1, 262144, 8388609)]
    public async Task RejectsInvalidLimits(int count, int perFile, long total)
    {
        var paths = Enumerable.Range(0, count)
            .Select(i => Path.Combine(_root, $"{i}.txt"));

        var result = await CreateHandler().HandleAsync(
            CreateCommand(paths, perFile, total),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error!.Code);
    }

    [Fact]
    public async Task PreCancelledCommandReturnsCancelled()
    {
        var path = Path.Combine(_root, "cancelled.txt");
        await File.WriteAllTextAsync(path, "data");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await CreateHandler().HandleAsync(
            CreateCommand(new[] { path }),
            cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CommandCancelled, result.Error!.Code);
    }

    [Fact]
    public async Task ConcurrencyNeverExceedsFour()
    {
        var paths = Enumerable.Range(0, 12)
            .Select(index => Path.Combine(_root, $"file-{index}.txt"))
            .ToList();
        foreach (var path in paths)
            await File.WriteAllTextAsync(path, "data");

        var handler = CreateHandler();
        var field = typeof(CommandHandler).GetField(
            "_fileSystemExecutor",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var executor = Assert.IsType<FileSystemExecutor>(field.GetValue(handler));
        var active = 0;
        var maximum = 0;

        executor.OnBeforeFileReadHook = async _ =>
        {
            var current = Interlocked.Increment(ref active);
            var observed = Volatile.Read(ref maximum);
            while (current > observed)
            {
                var previous = Interlocked.CompareExchange(
                    ref maximum,
                    current,
                    observed);
                if (previous == observed)
                    break;
                observed = previous;
            }

            await Task.Delay(30);
            Interlocked.Decrement(ref active);
        };

        var data = GetData(await handler.HandleAsync(
            CreateCommand(paths),
            CancellationToken.None));

        Assert.Equal(12, data.Succeeded);
        Assert.InRange(maximum, 2, 4);
    }
}
