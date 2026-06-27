using LocalMcp.Agent.Windows.FileSystem;

namespace LocalMcp.UnitTests;

public sealed class GitToolsTests
{
    [Fact]
    public void BuildGitFilterValidationArguments_RestrictsLookupToLocalRepositoryConfig()
    {
        var arguments = FileSystemExecutor.BuildGitFilterValidationArguments();

        Assert.Equal(
            new[]
            {
                "config",
                "--local",
                "--includes",
                "--name-only",
                "--get-regexp",
                "^filter\\..*\\.(clean|process)$"
            },
            arguments);
    }

    [Fact]
    public void ParseGitStatusPorcelain_ParsesModifiedUntrackedRenameAndConflict()
    {
        var output = string.Concat(
            " M src/app.cs", "\0",
            "?? scratch/new.txt", "\0",
            "R  src/new-name.cs", "\0", "src/old-name.cs", "\0",
            "UU src/conflict.cs", "\0");

        var entries = FileSystemExecutor.ParseGitStatusPorcelain(output);

        Assert.Equal(4, entries.Count);

        Assert.Equal("src/app.cs", entries[0].Path);
        Assert.Equal(" M", entries[0].Status);
        Assert.Equal(" ", entries[0].IndexStatus);
        Assert.Equal("M", entries[0].WorkTreeStatus);

        Assert.Equal("scratch/new.txt", entries[1].Path);
        Assert.True(entries[1].IsUntracked);

        Assert.Equal("src/new-name.cs", entries[2].Path);
        Assert.Equal("src/old-name.cs", entries[2].OriginalPath);
        Assert.Equal("R ", entries[2].Status);

        Assert.Equal("src/conflict.cs", entries[3].Path);
        Assert.True(entries[3].IsConflict);
    }

    [Fact]
    public void BuildUntrackedPatch_WithTrailingNewline_ProducesNewFilePatch()
    {
        var patch = FileSystemExecutor.BuildUntrackedPatch(
            "src/new file.txt",
            "first\nsecond\n");

        Assert.Contains("diff --git \"a/src/new file.txt\" \"b/src/new file.txt\"", patch);
        Assert.Contains("new file mode 100644", patch);
        Assert.Contains("--- /dev/null", patch);
        Assert.Contains("+++ \"b/src/new file.txt\"", patch);
        Assert.Contains("@@ -0,0 +1,2 @@", patch);
        Assert.Contains("+first\n+second\n", patch);
        Assert.DoesNotContain("No newline at end of file", patch);
    }

    [Fact]
    public void BuildUntrackedPatch_WithoutTrailingNewline_AddsMarker()
    {
        var patch = FileSystemExecutor.BuildUntrackedPatch("new.txt", "single line");

        Assert.Contains("@@ -0,0 +1,1 @@", patch);
        Assert.Contains("+single line\n", patch);
        Assert.Contains("\\ No newline at end of file", patch);
    }

    [Fact]
    public void BuildUntrackedPatch_EmptyFile_ProducesHeadersWithoutHunk()
    {
        var patch = FileSystemExecutor.BuildUntrackedPatch("empty.txt", string.Empty);

        Assert.Contains("new file mode 100644", patch);
        Assert.DoesNotContain("@@", patch);
    }
}
