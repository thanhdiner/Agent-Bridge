using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using LocalMcp.Gateway.Mcp;
using Xunit;

namespace LocalMcp.UnitTests;

/// <summary>
/// Validates that MCP tool annotations and public parameter schemas match the
/// agreed contract. No tool may expose internal types (CancellationToken,
/// HttpContext, ClaimsPrincipal, IServiceProvider, requestContext).
/// </summary>
public sealed class McpToolMetadataTests
{
    private static readonly Type ToolsType = typeof(FileSystemTools);

    private static MethodInfo GetToolMethod(string toolName)
    {
        var method = ToolsType.GetMethods()
            .FirstOrDefault(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        Assert.NotNull(method);
        return method!;
    }

    // ── Metadata / Annotation Assertions ─────────────────────────────────────

    [Theory]
    [InlineData("fs_read", true, false, true, false)]
    [InlineData("fs_read_range", true, false, true, false)]
    [InlineData("fs_list", true, false, true, false)]
    [InlineData("fs_tree", true, false, true, false)]
    [InlineData("fs_search", true, false, true, false)]
    [InlineData("fs_search_context", true, false, true, false)]
    [InlineData("fs_write", false, true, false, false)]
    [InlineData("fs_patch", false, true, false, false)]
    [InlineData("fs_mkdir", false, false, true, false)]
    [InlineData("fs_stat", true, false, true, false)]
    [InlineData("fs_batch_stat", true, false, true, false)]
    [InlineData("fs_move", false, false, false, false)]
    [InlineData("fs_copy", false, false, false, false)]
    [InlineData("fs_delete", false, true, false, false)]
    [InlineData("fs_rmdir", false, true, false, false)]
    public void ValidateToolAnnotations(
        string toolName,
        bool expectReadOnly,
        bool expectDestructive,
        bool expectIdempotent,
        bool expectOpenWorld)
    {
        var attr = GetToolMethod(toolName).GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(expectReadOnly, attr!.ReadOnly);
        Assert.Equal(expectDestructive, attr.Destructive);
        Assert.Equal(expectIdempotent, attr.Idempotent);
        Assert.Equal(expectOpenWorld, attr.OpenWorld);
    }

    [Fact]
    public void FsCopy_DescriptionRequiresUserConfirmation()
    {
        var description = GetToolMethod("fs_copy").GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(description);
        Assert.Contains("Ask the user for confirmation", description!.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FsRmdir_DescriptionRequiresUserConfirmation()
    {
        var description = GetToolMethod("fs_rmdir").GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(description);
        Assert.Contains("Ask the user for confirmation", description!.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FsDelete_DescriptionRequiresUserConfirmation()
    {
        var description = GetToolMethod("fs_delete").GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(description);
        Assert.Contains("Ask the user for confirmation", description!.Description, StringComparison.OrdinalIgnoreCase);
    }

    // ── Schema / Parameter Assertions ────────────────────────────────────────

    private static IEnumerable<string> GetParamNames(string toolName) =>
        GetToolMethod(toolName)
            .GetParameters()
            .Select(p => p.Name!);

    private static readonly HashSet<string> ForbiddenParamTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CancellationToken",
        "HttpContext",
        "DefaultHttpContext",
        "ClaimsPrincipal",
        "ClaimsIdentity",
        "IServiceProvider",
        "IHttpContextAccessor",
        "Object",   // catches object? requestContext
    };

    private void AssertNoInternalParams(string toolName)
    {
        var method = GetToolMethod(toolName);
        foreach (var p in method.GetParameters())
        {
            var typeName = p.ParameterType.Name;
            Assert.False(
                ForbiddenParamTypes.Contains(typeName),
                $"Tool '{toolName}' exposes internal parameter '{p.Name}' of type '{typeName}'");
        }
    }

    [Fact] public void FsRead_NoInternalParameters() => AssertNoInternalParams("fs_read");
    [Fact] public void FsReadRange_NoInternalParameters() => AssertNoInternalParams("fs_read_range");
    [Fact] public void FsList_NoInternalParameters() => AssertNoInternalParams("fs_list");
    [Fact] public void FsTree_NoInternalParameters() => AssertNoInternalParams("fs_tree");
    [Fact] public void FsSearch_NoInternalParameters() => AssertNoInternalParams("fs_search");
    [Fact] public void FsSearchContext_NoInternalParameters() => AssertNoInternalParams("fs_search_context");
    [Fact] public void FsWrite_NoInternalParameters() => AssertNoInternalParams("fs_write");
    [Fact] public void FsPatch_NoInternalParameters() => AssertNoInternalParams("fs_patch");
    [Fact] public void FsMkdir_NoInternalParameters() => AssertNoInternalParams("fs_mkdir");
    [Fact] public void FsStat_NoInternalParameters() => AssertNoInternalParams("fs_stat");
    [Fact] public void FsBatchStat_NoInternalParameters() => AssertNoInternalParams("fs_batch_stat");
    [Fact] public void FsMove_NoInternalParameters() => AssertNoInternalParams("fs_move");
    [Fact] public void FsCopy_NoInternalParameters() => AssertNoInternalParams("fs_copy");
    [Fact] public void FsDelete_NoInternalParameters() => AssertNoInternalParams("fs_delete");
    [Fact] public void FsRmdir_NoInternalParameters() => AssertNoInternalParams("fs_rmdir");

    [Fact]
    public void FsRead_HasExactSchema()
    {
        var names = GetParamNames("fs_read").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void FsReadRange_HasExactSchema()
    {
        var names = GetParamNames("fs_read_range").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Contains("startLine", names);
        Assert.Contains("lineCount", names);
        Assert.Equal(4, names.Count);
    }

    [Fact]
    public void FsList_HasExactSchema()
    {
        var names = GetParamNames("fs_list").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Contains("maxEntries", names);
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public void FsTree_HasExactSchema()
    {
        var names = GetParamNames("fs_tree").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Contains("maxDepth", names);
        Assert.Contains("maxEntries", names);
        Assert.Equal(4, names.Count);
    }

    [Fact]
    public void FsSearch_HasExactSchema()
    {
        var names = GetParamNames("fs_search").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Contains("query", names);
        Assert.Contains("maxResults", names);
        Assert.Contains("maxDepth", names);
        Assert.Equal(5, names.Count);
    }

    [Fact]
    public void FsSearchContext_HasExactSchema()
    {
        var names = GetParamNames("fs_search_context").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Contains("query", names);
        Assert.Contains("useRegex", names);
        Assert.Contains("caseSensitive", names);
        Assert.Contains("includeGlobs", names);
        Assert.Contains("excludeGlobs", names);
        Assert.Contains("contextBefore", names);
        Assert.Contains("contextAfter", names);
        Assert.Contains("maxResults", names);
        Assert.Contains("maxDepth", names);
        Assert.Equal(11, names.Count);
    }

    [Fact]
    public void FsWrite_HasExactSchema()
    {
        var names = GetParamNames("fs_write").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Contains("content", names);
        Assert.Contains("expectedSha256", names);
        Assert.Contains("createIfMissing", names);
        Assert.Equal(5, names.Count);
    }

    [Fact]
    public void FsPatch_HasExactSchema()
    {
        var names = GetParamNames("fs_patch").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Contains("expectedSha256", names);
        Assert.Contains("edits", names);
        Assert.Equal(4, names.Count);
    }

    [Fact]
    public void FsMkdir_HasExactSchema()
    {
        var names = GetParamNames("fs_mkdir").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Contains("recursive", names);
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public void FsStat_HasExactSchema()
    {
        var names = GetParamNames("fs_stat").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void FsBatchStat_HasExactSchema()
    {
        var names = GetParamNames("fs_batch_stat").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("paths", names);
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void FsMove_HasExactSchema()
    {
        var names = GetParamNames("fs_move").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Contains("destination", names);
        Assert.Contains("overwrite", names);
        Assert.Contains("expectedSha256", names);
        Assert.Equal(5, names.Count);
    }

    [Fact]
    public void FsCopy_HasExactSchema()
    {
        var names = GetParamNames("fs_copy").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Contains("destination", names);
        Assert.Contains("overwrite", names);
        Assert.Contains("expectedSourceSha256", names);
        Assert.Contains("recursive", names);
        Assert.Contains("maxEntries", names);
        Assert.Contains("maxTotalBytes", names);
        Assert.Equal(8, names.Count);
    }

    [Fact]
    public void FsRmdir_HasExactSchema()
    {
        var names = GetParamNames("fs_rmdir").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Contains("missingOk", names);
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public void FsDelete_HasExactSchema()
    {
        var names = GetParamNames("fs_delete").ToHashSet();
        Assert.Contains("deviceId", names);
        Assert.Contains("path", names);
        Assert.Contains("expectedSha256", names);
        Assert.Contains("missingOk", names);
        Assert.Equal(4, names.Count);
    }
}
