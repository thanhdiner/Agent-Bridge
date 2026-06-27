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
    public void DetectProjectType_PrefersPythonForFrontendHybrid()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[build-system]");
            File.WriteAllText(Path.Combine(root, "package.json"), "{}");

            Assert.Equal("python", FileSystemExecutor.DetectProjectType(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DetectProjectType_DetectsGoModule()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/sample");

            Assert.Equal("go", FileSystemExecutor.DetectProjectType(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildProjectStepPlans_PythonPrefersVenvTools()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[build-system]");
            var scripts = Path.Combine(root, ".venv", "Scripts");
            Directory.CreateDirectory(scripts);
            foreach (var executable in new[] { "python.exe", "pytest.exe", "ruff.exe", "mypy.exe" })
                File.WriteAllText(Path.Combine(scripts, executable), string.Empty);

            var plans = FileSystemExecutor.BuildProjectStepPlans(
                root,
                "python",
                ["build", "test", "lint", "typecheck"],
                "Debug");

            Assert.Equal(4, plans.Count);
            Assert.Equal(Path.Combine(".venv", "Scripts", "python.exe"), plans[0].Executable);
            Assert.Equal(new[] { "-m", "build", "--no-isolation" }, plans[0].Arguments);
            Assert.Equal("pytest", plans[1].Toolchain);
            Assert.Equal(Path.Combine(".venv", "Scripts", "pytest.exe"), plans[1].Executable);
            Assert.Contains(plans[1].Alternatives, candidate =>
                candidate.Toolchain == "python-unittest" && candidate.Executable == "python.exe");
            Assert.Equal("ruff", plans[2].Toolchain);
            Assert.Equal("mypy", plans[3].Toolchain);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildProjectStepPlans_PythonUsesFixedFallbackOrderWithoutVenv()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "requirements.txt"), "requests==2.32.0");

            var plans = FileSystemExecutor.BuildProjectStepPlans(
                root,
                "python",
                ["build", "test", "lint", "typecheck"],
                "Debug");

            Assert.False(plans[0].Supported);
            Assert.Equal("build_manifest_not_found", plans[0].SkipReason);
            Assert.Equal("pytest.exe", plans[1].Executable);
            Assert.Equal(
                new[] { "python-unittest", "python-unittest" },
                plans[1].Alternatives.Select(candidate => candidate.Toolchain));
            Assert.Equal("ruff.exe", plans[2].Executable);
            Assert.Equal(
                new[] { "flake8" },
                plans[2].Alternatives.Select(candidate => candidate.Toolchain).Distinct());
            Assert.Equal("mypy.exe", plans[3].Executable);
            Assert.Equal(
                new[] { "pyright" },
                plans[3].Alternatives.Select(candidate => candidate.Toolchain).Distinct());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildProjectStepPlans_GoUsesFixedCommands()
    {
        var plans = FileSystemExecutor.BuildProjectStepPlans(
            Path.GetTempPath(),
            "go",
            ["build", "test", "lint", "typecheck"],
            "Debug");

        Assert.All(plans, plan => Assert.Equal("go.exe", plan.Executable));
        Assert.Equal(new[] { "build", "./..." }, plans[0].Arguments);
        Assert.Equal(new[] { "test", "./..." }, plans[1].Arguments);
        Assert.Equal(new[] { "vet", "./..." }, plans[2].Arguments);
        Assert.Equal(new[] { "test", "-run=^$", "./..." }, plans[3].Arguments);
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
