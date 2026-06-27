using System.Security.Cryptography;
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

public sealed class MultiFileEditTests : IDisposable
{
    private readonly string _root;

    public MultiFileEditTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "LocalMcp_MultiEdit_" + Guid.NewGuid());
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
        return new CommandHandler(policy, executor, NullLogger<CommandHandler>.Instance);
    }

    private static MultiFilePatchCommand Command(params MultiFilePatchItem[] items) => new()
    {
        CommandId = Guid.NewGuid(),
        DeviceId = "test-device",
        CreatedAt = DateTimeOffset.UtcNow,
        Items = items.ToList()
    };

    private static MultiFilePatchItem Item(
        string path,
        string hash,
        string oldText,
        string newText) => new()
    {
        Path = path,
        ExpectedSha256 = hash,
        Edits = new List<PatchEdit>
        {
            new() { OldText = oldText, NewText = newText, ReplaceAll = false }
        }
    };

    private static async Task<string> HashAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static MultiFilePatchResult Data(CommandResult<JsonElement> result)
    {
        Assert.True(result.Success);
        return Assert.IsType<JsonElement>(result.Data)
            .Deserialize<MultiFilePatchResult>(JsonOptions.Default)!;
    }

    [Fact]
    public async Task AppliesEditsInInputOrder()
    {
        var first = Path.Combine(_root, "first.txt");
        var second = Path.Combine(_root, "second.txt");
        await File.WriteAllTextAsync(first, "alpha");
        await File.WriteAllTextAsync(second, "beta");

        var data = Data(await CreateHandler().HandleAsync(
            Command(
                Item(second, await HashAsync(second), "beta", "two"),
                Item(first, await HashAsync(first), "alpha", "one")),
            CancellationToken.None));

        Assert.Equal(2, data.Succeeded);
        Assert.Equal(0, data.Failed);
        Assert.Equal(new[] { second, first }, data.Items.Select(item => item.Path));
        Assert.Equal("two", await File.ReadAllTextAsync(second));
        Assert.Equal("one", await File.ReadAllTextAsync(first));
        Assert.Equal(1, data.Items[0].Data!.EditsApplied);
        Assert.Equal(1, data.Items[0].Data!.ReplacementsMade);
        Assert.NotEqual(data.Items[0].Data!.PreviousSha256, data.Items[0].Data!.Sha256);
    }

    [Fact]
    public async Task FailureDoesNotRollbackSuccessfulFile()
    {
        var valid = Path.Combine(_root, "valid.txt");
        var conflict = Path.Combine(_root, "conflict.txt");
        await File.WriteAllTextAsync(valid, "before");
        await File.WriteAllTextAsync(conflict, "current");

        var data = Data(await CreateHandler().HandleAsync(
            Command(
                Item(valid, await HashAsync(valid), "before", "after"),
                Item(conflict, new string('0', 64), "current", "changed")),
            CancellationToken.None));

        Assert.Equal(1, data.Succeeded);
        Assert.Equal(1, data.Failed);
        Assert.Equal("after", await File.ReadAllTextAsync(valid));
        Assert.Equal("current", await File.ReadAllTextAsync(conflict));
        Assert.Equal(ErrorCodes.FileConflict, data.Items[1].Error!.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task RejectsInvalidItemCount(int count)
    {
        var items = Enumerable.Range(0, count)
            .Select(index => Item(
                Path.Combine(_root, $"{index}.txt"),
                new string('0', 64),
                "old",
                "new"))
            .ToArray();

        var result = await CreateHandler().HandleAsync(
            Command(items),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error!.Code);
    }

    [Fact]
    public async Task MissingExpectedHashIsItemFailure()
    {
        var path = Path.Combine(_root, "hash-required.txt");
        await File.WriteAllTextAsync(path, "old");

        var data = Data(await CreateHandler().HandleAsync(
            Command(Item(path, string.Empty, "old", "new")),
            CancellationToken.None));

        Assert.Equal(0, data.Succeeded);
        Assert.Equal(1, data.Failed);
        Assert.Equal(ErrorCodes.ExpectedHashRequired, data.Items[0].Error!.Code);
        Assert.Equal("old", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PreCancelledCommandReturnsCancelled()
    {
        var path = Path.Combine(_root, "cancelled.txt");
        await File.WriteAllTextAsync(path, "old");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await CreateHandler().HandleAsync(
            Command(Item(path, await HashAsync(path), "old", "new")),
            cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CommandCancelled, result.Error!.Code);
        Assert.Equal("old", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ConcurrencyNeverExceedsFour()
    {
        var items = new List<MultiFilePatchItem>();
        for (var index = 0; index < 12; index++)
        {
            var path = Path.Combine(_root, $"concurrent-{index}.txt");
            var content = $"value-{index}";
            await File.WriteAllTextAsync(path, content);
            items.Add(Item(path, await HashAsync(path), content, $"updated-{index}"));
        }

        var handler = CreateHandler();
        var active = 0;
        var maximum = 0;
        handler.OnBeforeMultiFileEditHook = async _ =>
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

        var data = Data(await handler.HandleAsync(
            Command(items.ToArray()),
            CancellationToken.None));

        Assert.Equal(12, data.Succeeded);
        Assert.InRange(maximum, 2, 4);
    }
}
