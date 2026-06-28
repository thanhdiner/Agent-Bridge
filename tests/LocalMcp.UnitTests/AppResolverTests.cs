using LocalMcp.Agent.Windows.AppLaunch;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalMcp.UnitTests;

public sealed class AppResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"localmcp-app-resolver-{Guid.NewGuid():N}");

    public AppResolverTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("Chrome", "chrome")]
    [InlineData(" chrome.exe ", "chrome")]
    [InlineData("Visual   Studio Code", "visual studio code")]
    public void NormalizeAppId_AcceptsNames(string input, string expected)
    {
        Assert.True(AppResolver.TryNormalizeAppId(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\Apps\\Chrome.exe")]
    [InlineData("..\\Chrome")]
    public void NormalizeAppId_RejectsInvalidValues(string input) =>
        Assert.False(AppResolver.TryNormalizeAppId(input, out _));

    [Fact]
    public void Constructor_DoesNotTouchCacheFile()
    {
        var cachePath = Path.Combine(_root, "cache", "apps.json");
        _ = CreateResolver(cachePath, new Dictionary<string, string>());

        Assert.False(File.Exists(cachePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(cachePath)));
    }

    [Fact]
    public async Task ResolveAsync_ConfiguredAliasThenMemoryCache_Succeeds()
    {
        var executable = CreateFakeGuiExecutable("Example.exe");
        var cachePath = Path.Combine(_root, "cache.json");
        var resolver = CreateResolver(
            cachePath,
            new Dictionary<string, string> { ["example"] = executable });

        var cold = await resolver.ResolveAsync(
            "example",
            refresh: false,
            Guid.NewGuid(),
            CancellationToken.None);
        var warm = await resolver.ResolveAsync(
            "example",
            refresh: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(cold.Success);
        Assert.True(cold.Data!.Resolved);
        Assert.False(cold.Data.CacheHit);
        Assert.Equal("configured-alias", cold.Data.Source);
        Assert.Equal(Path.GetFullPath(executable), cold.Data.ExecutablePath, ignoreCase: true);
        Assert.True(File.Exists(cachePath));

        Assert.True(warm.Success);
        Assert.True(warm.Data!.Resolved);
        Assert.True(warm.Data.CacheHit);
        Assert.Equal(cold.Data.ExecutablePath, warm.Data.ExecutablePath);
    }

    [Fact]
    public async Task ResolveAsync_NewResolverLoadsPersistentCache()
    {
        var executable = CreateFakeGuiExecutable("Persistent.exe");
        var cachePath = Path.Combine(_root, "persistent-cache.json");
        var first = CreateResolver(
            cachePath,
            new Dictionary<string, string> { ["persistent"] = executable });
        var firstResult = await first.ResolveAsync(
            "persistent",
            refresh: false,
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.True(firstResult.Data!.Resolved);

        var second = CreateResolver(cachePath, new Dictionary<string, string>());
        var secondResult = await second.ResolveAsync(
            "persistent",
            refresh: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(secondResult.Success);
        Assert.True(secondResult.Data!.Resolved);
        Assert.True(secondResult.Data.CacheHit);
        Assert.Equal(Path.GetFullPath(executable), secondResult.Data.ExecutablePath, ignoreCase: true);
    }

    [Fact]
    public async Task ResolveAsync_RefreshRediscoverOnlyRequestedAlias()
    {
        var firstExecutable = CreateFakeGuiExecutable("First.exe");
        var secondExecutable = CreateFakeGuiExecutable("Second.exe");
        var aliases = new Dictionary<string, string> { ["example"] = firstExecutable };
        var resolver = CreateResolver(Path.Combine(_root, "refresh-cache.json"), aliases);

        var first = await resolver.ResolveAsync(
            "example",
            refresh: false,
            Guid.NewGuid(),
            CancellationToken.None);
        aliases["example"] = secondExecutable;
        var refreshed = await resolver.ResolveAsync(
            "example",
            refresh: true,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(firstExecutable), first.Data!.ExecutablePath, ignoreCase: true);
        Assert.True(refreshed.Data!.Resolved);
        Assert.True(refreshed.Data.Refreshed);
        Assert.False(refreshed.Data.CacheHit);
        Assert.Equal(Path.GetFullPath(secondExecutable), refreshed.Data.ExecutablePath, ignoreCase: true);
    }

    [Fact]
    public async Task ResolveAsync_UnknownAlias_ReturnsUnresolvedSuccess()
    {
        var resolver = CreateResolver(
            Path.Combine(_root, "unknown-cache.json"),
            new Dictionary<string, string>());

        var result = await resolver.ResolveAsync(
            $"missing-{Guid.NewGuid():N}",
            refresh: false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Data!.Resolved);
        Assert.Null(result.Data.ExecutablePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private AppResolver CreateResolver(
        string cachePath,
        Dictionary<string, string> aliases)
    {
        var resolverOptions = new AppResolverOptions
        {
            CachePath = cachePath,
            MaxCacheEntries = 16,
            MaxStartMenuShortcuts = 0,
            Aliases = aliases
        };
        return new AppResolver(
            Options.Create(resolverOptions),
            Options.Create(new AppLaunchOptions()),
            NullLogger<AppResolver>.Instance);
    }

    private string CreateFakeGuiExecutable(string fileName)
    {
        var path = Path.Combine(_root, fileName);
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
        writer.Write((ushort)2);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
