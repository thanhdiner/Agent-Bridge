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

    [Fact]
    public void ParseGitLogOutput_ParsesMetadataBodyAndShortStats()
    {
        var output = string.Concat(
            "\u001e", new string('a', 40), "\0abc1234\0", new string('b', 40), " ", new string('c', 40),
            "\0Ada Lovelace\0ada@example.test\02026-06-27T10:15:30+07:00\0Add history tools\0",
            "Body line one\nBody line two\n\0\n",
            " 2 files changed, 12 insertions(+), 3 deletions(-)\n");

        var commits = FileSystemExecutor.ParseGitLogOutput(output, includeStats: true);

        var commit = Assert.Single(commits);
        Assert.Equal(new string('a', 40), commit.Hash);
        Assert.Equal("abc1234", commit.ShortHash);
        Assert.Equal(new[] { new string('b', 40), new string('c', 40) }, commit.Parents);
        Assert.Equal("Ada Lovelace", commit.AuthorName);
        Assert.Equal("ada@example.test", commit.AuthorEmail);
        Assert.Equal("Add history tools", commit.Subject);
        Assert.Equal("Body line one\nBody line two", commit.Body);
        Assert.Equal(2, commit.FilesChanged);
        Assert.Equal(12, commit.Insertions);
        Assert.Equal(3, commit.Deletions);
    }

    [Fact]
    public void ParseGitLogOutput_WithoutStats_LeavesStatsNull()
    {
        var output = string.Concat(
            "\u001e", new string('d', 40), "\0def5678\0\0Grace Hopper\0grace@example.test\0",
            "2026-06-27T10:15:30Z\0Initial commit\0\0");

        var commit = Assert.Single(FileSystemExecutor.ParseGitLogOutput(output, includeStats: false));

        Assert.Empty(commit.Parents);
        Assert.Null(commit.FilesChanged);
        Assert.Null(commit.Insertions);
        Assert.Null(commit.Deletions);
    }

    [Fact]
    public void ParseGitNumStat_CountsTextAndBinaryFiles()
    {
        var output = "10\t2\tsrc/a.cs\0-\t-\tassets/image.png\03\t0\tREADME.md\0";

        var stats = FileSystemExecutor.ParseGitNumStat(output);

        Assert.Equal(3, stats.FilesChanged);
        Assert.Equal(13, stats.Insertions);
        Assert.Equal(2, stats.Deletions);
    }
}
