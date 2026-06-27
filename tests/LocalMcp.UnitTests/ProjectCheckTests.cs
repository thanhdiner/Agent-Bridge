using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Contracts.Commands;

namespace LocalMcp.UnitTests;

public sealed class ProjectCheckTests
{
    [Fact]
    public void DetectProjectType_PrefersPhpForLaravelHybrid()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "composer.json"), "{}");
            File.WriteAllText(Path.Combine(root, "package.json"), "{}");

            Assert.Equal("php", FileSystemExecutor.DetectProjectType(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildProjectStepPlans_NodeUsesPnpmAndSkipsMissingScript()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "package.json"),
                """
                {
                  "scripts": {
                    "build": "vite build",
                    "test": "vitest run"
                  }
                }
                """);
            File.WriteAllText(Path.Combine(root, "pnpm-lock.yaml"), "lockfileVersion: 9");

            var plans = FileSystemExecutor.BuildProjectStepPlans(
                root,
                "node",
                ["build", "test", "lint"],
                "Debug");

            Assert.Equal(3, plans.Count);
            Assert.Equal("pnpm", plans[0].Toolchain);
            Assert.Equal("pnpm.cmd", plans[0].Executable);
            Assert.Equal(new[] { "run", "build" }, plans[0].Arguments);
            Assert.True(plans[0].Supported);
            Assert.True(plans[1].Supported);
            Assert.False(plans[2].Supported);
            Assert.Equal("script_not_found:lint", plans[2].SkipReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildProjectStepPlans_DotNetTestUsesNoBuildAfterBuild()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "Sample.sln"), string.Empty);

            var plans = FileSystemExecutor.BuildProjectStepPlans(
                root,
                "dotnet",
                ["build", "test"],
                "Release");

            Assert.Equal(2, plans.Count);
            Assert.Contains("--configuration", plans[0].Arguments);
            Assert.Contains("Release", plans[0].Arguments);
            Assert.Contains("--no-build", plans[1].Arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildProjectStepPlans_PhpCanSkipBuildAndStillPlanTests()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "composer.json"), "{}");
            File.WriteAllText(Path.Combine(root, "artisan"), string.Empty);

            var plans = FileSystemExecutor.BuildProjectStepPlans(
                root,
                "php",
                ["build", "test"],
                "Debug");

            Assert.Equal(2, plans.Count);
            Assert.False(plans[0].Supported);
            Assert.Equal("build_step_not_found", plans[0].SkipReason);
            Assert.True(plans[1].Supported);
            Assert.Equal("php-artisan", plans[1].Toolchain);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildProjectStepPlans_RustReleaseUsesReleaseFlag()
    {
        var plans = FileSystemExecutor.BuildProjectStepPlans(
            Path.GetTempPath(),
            "rust",
            ["build", "typecheck"],
            "Release");

        Assert.All(plans, plan => Assert.Contains("--release", plan.Arguments));
    }

    [Fact]
    public void BuildTrustedCommandScriptInvocation_UsesCmdSafeOuterQuotes()
    {
        var command = FileSystemExecutor.BuildTrustedCommandScriptInvocation(
            "C:/Program Files/nodejs/npm.cmd",
            ["run", "build"]);

        Assert.StartsWith("\"\"", command, StringComparison.Ordinal);
        Assert.EndsWith("\"\"", command, StringComparison.Ordinal);
        Assert.Contains("\"C:/Program Files/nodejs/npm.cmd\"", command, StringComparison.Ordinal);
        Assert.Contains("\"run\" \"build\"", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTrustedCommandScriptInvocation_RejectsExpansionTokens()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FileSystemExecutor.BuildTrustedCommandScriptInvocation(
                "npm.cmd",
                ["run", "%PATH%"]));
    }

    [Fact]
    public void AgentCommandTimeouts_ProjectCheckAddsTransportBuffer()
    {
        var command = new ProjectCheckCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            Path = "C:/src",
            TimeoutSeconds = 300
        };

        Assert.Equal(
            TimeSpan.FromSeconds(315),
            AgentCommandTimeouts.GetTimeout(command));
    }

    [Fact]
    public void DetectProjectType_UnknownDirectoryReturnsNull()
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.Null(FileSystemExecutor.DetectProjectType(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "LocalMcp.ProjectCheckTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
