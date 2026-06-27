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
using Xunit;

namespace LocalMcp.UnitTests;

public sealed class BatchStatTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileAccessOptions _options;

    public BatchStatTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_BatchStatTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);

        _options = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            WritableRoots = new List<string> { _tempRoot },
            DeniedSegments = new List<string> { ".git", "bin", "obj" },
            DeniedFileNames = new List<string> { ".env", "id_rsa" },
            DeniedWriteFileNames = new List<string>(),
            DeniedWriteExtensions = new List<string>(),
            MaxReadBytes = 2_097_152,
            MaxWriteBytes = 524_288
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private (CommandHandler Handler, FileSystemExecutor Executor) MakeHandler()
    {
        var policy = new PathPolicy(Options.Create(_options));
        var executor = new FileSystemExecutor(
            policy,
            Options.Create(_options),
            NullLogger<FileSystemExecutor>.Instance);
        var handler = new CommandHandler(
            policy,
            executor,
            NullLogger<CommandHandler>.Instance);
        return (handler, executor);
    }

    private static BatchStatCommand MakeCommand(IReadOnlyList<string> paths) =>
        new()
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "test-device",
            CreatedAt = DateTimeOffset.UtcNow,
            Paths = paths.ToList()
        };

    private static BatchStatResult DeserializeResult(CommandResult<JsonElement> result)
    {
        Assert.True(result.Success);
        var element = Assert.IsType<JsonElement>(result.Data);
        return element.Deserialize<BatchStatResult>(JsonOptions.Default)!;
    }

    [Fact]
    public async Task BatchStat_FileDirectoryAndMissingPath_ReturnsResultsInInputOrder()
    {
        var file = Path.Combine(_tempRoot, "file.txt");
        var directory = Path.Combine(_tempRoot, "directory");
        var missing = Path.Combine(_tempRoot, "missing.txt");
        File.WriteAllText(file, "hello");
        Directory.CreateDirectory(directory);
        var (handler, _) = MakeHandler();

        var result = await handler.HandleAsync(
            MakeCommand(new[] { directory, missing, file }),
            CancellationToken.None);
        var data = DeserializeResult(result);

        Assert.Equal(3, data.Succeeded);
        Assert.Equal(0, data.Failed);
        Assert.Equal(new[] { directory, missing, file }, data.Items.Select(item => item.Path).ToArray());
        Assert.Equal("directory", data.Items[0].Data!.Type);
        Assert.False(data.Items[1].Data!.Exists);
        Assert.Equal("file", data.Items[2].Data!.Type);
    }

    [Fact]
    public async Task BatchStat_DeniedPath_DoesNotFailOtherItems()
    {
        var allowed = Path.Combine(_tempRoot, "allowed.txt");
        var deniedDirectory = Path.Combine(_tempRoot, ".git");
        var denied = Path.Combine(deniedDirectory, "blocked.txt");
        File.WriteAllText(allowed, "allowed");
        Directory.CreateDirectory(deniedDirectory);
        File.WriteAllText(denied, "blocked");
        var (handler, _) = MakeHandler();

        var result = await handler.HandleAsync(
            MakeCommand(new[] { allowed, denied }),
            CancellationToken.None);
        var data = DeserializeResult(result);

        Assert.Equal(1, data.Succeeded);
        Assert.Equal(1, data.Failed);
        Assert.True(data.Items[0].Success);
        Assert.NotNull(data.Items[0].Data);
        Assert.Null(data.Items[0].Error);
        Assert.False(data.Items[1].Success);
        Assert.Null(data.Items[1].Data);
        Assert.Equal(ErrorCodes.AccessDenied, data.Items[1].Error!.Code);
    }

    [Fact]
    public async Task BatchStat_InvalidPath_IsReturnedAsItemFailure()
    {
        var allowed = Path.Combine(_tempRoot, "valid.txt");
        File.WriteAllText(allowed, "valid");
        var (handler, _) = MakeHandler();

        var result = await handler.HandleAsync(
            MakeCommand(new[] { allowed, " " }),
            CancellationToken.None);
        var data = DeserializeResult(result);

        Assert.Equal(1, data.Succeeded);
        Assert.Equal(1, data.Failed);
        Assert.True(data.Items[0].Success);
        Assert.False(data.Items[1].Success);
        Assert.Equal(ErrorCodes.InvalidPath, data.Items[1].Error!.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task BatchStat_InvalidPathCount_ReturnsInvalidRequest(int count)
    {
        var paths = Enumerable.Range(0, count)
            .Select(index => Path.Combine(_tempRoot, $"item-{index}.txt"))
            .ToList();
        var (handler, _) = MakeHandler();

        var result = await handler.HandleAsync(MakeCommand(paths), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error!.Code);
    }

    [Fact]
    public async Task BatchStat_PreCancelled_ReturnsCommandCancelled()
    {
        var file = Path.Combine(_tempRoot, "cancelled.txt");
        File.WriteAllText(file, "data");
        var (handler, _) = MakeHandler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await handler.HandleAsync(MakeCommand(new[] { file }), cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CommandCancelled, result.Error!.Code);
    }

    [Fact]
    public async Task BatchStat_ConcurrencyNeverExceedsEight()
    {
        var paths = Enumerable.Range(0, 16)
            .Select(index => Path.Combine(_tempRoot, $"file-{index}.txt"))
            .ToList();
        foreach (var path in paths)
            File.WriteAllText(path, "data");

        var (handler, executor) = MakeHandler();
        var active = 0;
        var maximum = 0;
        executor.OnBeforeContentReadHook = async _ =>
        {
            var current = Interlocked.Increment(ref active);
            var observed = Volatile.Read(ref maximum);
            while (current > observed)
            {
                var previous = Interlocked.CompareExchange(ref maximum, current, observed);
                if (previous == observed)
                    break;
                observed = previous;
            }

            await Task.Delay(30);
            Interlocked.Decrement(ref active);
        };

        var result = await handler.HandleAsync(MakeCommand(paths), CancellationToken.None);
        var data = DeserializeResult(result);

        Assert.Equal(16, data.Succeeded);
        Assert.InRange(maximum, 1, 8);
    }
}
