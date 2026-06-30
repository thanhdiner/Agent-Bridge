using LocalMcp.Agent.Windows.Security;
using LocalMcp.Agent.Windows.Workspaces;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Options;

namespace LocalMcp.UnitTests;

public sealed class WorkspaceResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AgentBridgeWorkspaceTests",
        Guid.NewGuid().ToString("N"));

    public WorkspaceResolverTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void List_Returns_Aliases_In_Stable_Order_With_Effective_Access()
    {
        var resolver = CreateResolver(new Dictionary<string, WorkspaceDefinition>
        {
            ["work"] = new() { Path = _root, Writable = false },
            ["main"] = new() { Path = _root, Writable = true, Description = "Primary workspace" }
        });

        var result = resolver.List();

        Assert.Equal(["main", "work"], result.Workspaces.Select(item => item.Alias));
        Assert.True(result.Workspaces[0].Available);
        Assert.True(result.Workspaces[0].Allowed);
        Assert.True(result.Workspaces[0].Writable);
        Assert.False(result.Workspaces[1].Writable);
    }

    [Fact]
    public void Resolve_Combines_Alias_And_Relative_Path()
    {
        var nested = Path.Combine(_root, "src", "file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "ok");
        var resolver = CreateResolver();

        var outcome = resolver.Resolve("main", "src/file.txt", requireWritable: false);

        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.Data);
        Assert.Equal(Path.GetFullPath(nested), outcome.Data!.AbsolutePath);
        Assert.Equal(Path.Combine("src", "file.txt"), outcome.Data.RelativePath);
        Assert.Equal("file", outcome.Data.EntryType);
        Assert.True(outcome.Data.Exists);
    }

    [Fact]
    public void Resolve_Allows_A_Missing_Child_Without_Authorizing_An_Operation()
    {
        var resolver = CreateResolver();

        var outcome = resolver.Resolve("main", "new/future.txt", requireWritable: true);

        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.Data);
        Assert.False(outcome.Data!.Exists);
        Assert.Equal("missing", outcome.Data.EntryType);
        Assert.True(outcome.Data.Writable);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    public void Resolve_Rejects_Traversal_Outside_The_Workspace(string relativePath)
    {
        var resolver = CreateResolver();

        var outcome = resolver.Resolve("main", relativePath, requireWritable: false);

        Assert.Null(outcome.Data);
        Assert.Equal(ErrorCodes.WorkspacePathOutsideRoot, outcome.Error?.Code);
    }

    [Fact]
    public void Resolve_Rejects_An_Absolute_Path()
    {
        var resolver = CreateResolver();

        var outcome = resolver.Resolve("main", Path.Combine(_root, "file.txt"), requireWritable: false);

        Assert.Null(outcome.Data);
        Assert.Equal(ErrorCodes.WorkspacePathInvalid, outcome.Error?.Code);
    }

    [Fact]
    public void Resolve_Rejects_Unknown_Alias()
    {
        var resolver = CreateResolver();

        var outcome = resolver.Resolve("missing", "README.md", requireWritable: false);

        Assert.Null(outcome.Data);
        Assert.Equal(ErrorCodes.WorkspaceNotFound, outcome.Error?.Code);
    }

    [Fact]
    public void Resolve_Enforces_Read_Only_Workspace()
    {
        var resolver = CreateResolver(new Dictionary<string, WorkspaceDefinition>
        {
            ["main"] = new() { Path = _root, Writable = false }
        });

        var outcome = resolver.Resolve("main", "README.md", requireWritable: true);

        Assert.Null(outcome.Data);
        Assert.Equal(ErrorCodes.WorkspaceReadOnly, outcome.Error?.Code);
    }

    [Fact]
    public void Resolve_Uses_The_Resolved_Path_For_Partial_Write_Access()
    {
        var writableChild = Path.Combine(_root, "writable");
        Directory.CreateDirectory(writableChild);
        var resolver = CreateResolver(writableRoots: [writableChild]);

        var allowed = resolver.Resolve("main", "writable/new.txt", requireWritable: true);
        var denied = resolver.Resolve("main", "readonly/new.txt", requireWritable: true);

        Assert.Null(allowed.Error);
        Assert.True(allowed.Data!.Writable);
        Assert.Null(denied.Data);
        Assert.Equal(ErrorCodes.WorkspaceReadOnly, denied.Error?.Code);
    }

    [Fact]
    public void Resolve_Is_Case_Insensitive_For_Aliases()
    {
        var resolver = CreateResolver();

        var outcome = resolver.Resolve("MAIN", "README.md", requireWritable: false);

        Assert.Null(outcome.Error);
        Assert.Equal("main", outcome.Data?.Alias);
    }

    [Fact]
    public void Resolve_Rejects_Workspace_Outside_Allowed_Roots()
    {
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var resolver = CreateResolver(
                new Dictionary<string, WorkspaceDefinition>
                {
                    ["outside"] = new() { Path = outside, Writable = true }
                });

            var outcome = resolver.Resolve("outside", string.Empty, requireWritable: false);

            Assert.Null(outcome.Data);
            Assert.Equal(ErrorCodes.WorkspaceNotAllowed, outcome.Error?.Code);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private WorkspaceResolver CreateResolver(
        Dictionary<string, WorkspaceDefinition>? aliases = null,
        IReadOnlyList<string>? writableRoots = null)
    {
        var workspaceOptions = new WorkspaceOptions
        {
            Aliases = aliases ?? new Dictionary<string, WorkspaceDefinition>
            {
                ["main"] = new() { Path = _root, Writable = true }
            }
        };
        var fileOptions = new FileAccessOptions
        {
            AllowedRoots = [_root],
            WritableRoots = writableRoots?.ToList() ?? [_root]
        };

        return new WorkspaceResolver(
            Options.Create(workspaceOptions),
            Options.Create(fileOptions));
    }
}
