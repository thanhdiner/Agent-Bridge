using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.FileSystem;

public sealed partial class FileSystemExecutor
{
    private const int MaxProjectManifestBytes = 1_048_576;

    private static readonly Encoding ProjectOutputEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    private static readonly Encoding ProjectManifestEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly HashSet<string> SupportedProjectTypes = new(
        ["auto", "dotnet", "node", "rust", "php", "python", "go"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> SupportedProjectSteps = new(
        ["build", "test", "lint", "typecheck"],
        StringComparer.Ordinal);

    public async Task<CommandResult<ProjectVerifyResult>> ProjectCheckAsync(
        string path,
        string projectType,
        IReadOnlyList<string> steps,
        string configuration,
        int timeoutSeconds,
        int maxOutputBytes,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var normalizedProjectType = projectType?.Trim().ToLowerInvariant() ?? string.Empty;
        var normalizedSteps = steps
            .Select(step => step?.Trim().ToLowerInvariant() ?? string.Empty)
            .ToList();
        var normalizedConfiguration = string.Equals(
            configuration,
            "Release",
            StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";

        var validationError = ValidateProjectCheckRequest(
            normalizedProjectType,
            normalizedSteps,
            configuration,
            timeoutSeconds,
            maxOutputBytes);
        if (validationError is not null)
            return ProjectCheckFailure(commandId, validationError.Code, validationError.Message);

        if (!Directory.Exists(path))
        {
            return ProjectCheckFailure(
                commandId,
                ErrorCodes.DirectoryNotFound,
                "The project directory does not exist.");
        }

        try
        {
            var detectedProjectType = ResolveProjectType(path, normalizedProjectType);
            var plans = BuildProjectStepPlans(
                path,
                detectedProjectType,
                normalizedSteps,
                normalizedConfiguration);

            var results = new List<ProjectVerifyStepResult>(plans.Count);
            var bytesReturned = 0;
            var stopAfterFailure = false;

            using var timeoutSource = new CancellationTokenSource(
                TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (timeoutSource.IsCancellationRequested)
                {
                    results.Add(CreateSkippedStep(plan, "verification_timeout", timedOut: true));
                    stopAfterFailure = true;
                    continue;
                }

                if (stopAfterFailure)
                {
                    results.Add(CreateSkippedStep(plan, "previous_step_failed", timedOut: false));
                    continue;
                }

                if (!plan.Supported)
                {
                    results.Add(CreateSkippedStep(
                        plan,
                        plan.SkipReason ?? "step_not_available",
                        timedOut: false));
                    continue;
                }

                var remainingOutputBytes = Math.Max(0, maxOutputBytes - bytesReturned);
                var stepResult = await RunProjectStepAsync(
                    path,
                    plan,
                    remainingOutputBytes,
                    linkedSource.Token,
                    cancellationToken,
                    timeoutSource.Token);

                results.Add(stepResult);
                bytesReturned += ProjectOutputEncoding.GetByteCount(stepResult.Output);

                if (!stepResult.Success)
                    stopAfterFailure = true;
            }

            var toolchain = string.Join(
                "+",
                results
                    .Select(result => result.Toolchain)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(toolchain))
                toolchain = detectedProjectType;

            var data = new ProjectVerifyResult
            {
                WorkingDirectory = path,
                DetectedProjectType = detectedProjectType,
                DetectedToolchain = toolchain,
                RequestedSteps = normalizedSteps,
                Steps = results,
                Success = results.Count == normalizedSteps.Count &&
                    results.All(result => result.Success),
                TimedOut = results.Any(result => result.TimedOut),
                Truncated = results.Any(result => result.Truncated),
                BytesReturned = bytesReturned
            };

            return new CommandResult<ProjectVerifyResult>
            {
                CommandId = commandId,
                Success = true,
                Data = data
            };
        }
        catch (OperationCanceledException)
        {
            return ProjectCheckFailure(
                commandId,
                ErrorCodes.CommandCancelled,
                "The project check command was cancelled.");
        }
        catch (ProjectCheckException ex)
        {
            return ProjectCheckFailure(commandId, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected project check failure for command {CommandId}",
                commandId);

            return ProjectCheckFailure(
                commandId,
                ErrorCodes.InternalError,
                "An unexpected error occurred while checking the project.");
        }
    }

    internal static string? DetectProjectType(string path)
    {
        if (HasDotNetMarker(path))
            return "dotnet";
        if (File.Exists(Path.Combine(path, "Cargo.toml")))
            return "rust";
        if (File.Exists(Path.Combine(path, "go.mod")))
            return "go";
        if (File.Exists(Path.Combine(path, "composer.json")) ||
            File.Exists(Path.Combine(path, "artisan")))
        {
            return "php";
        }
        if (HasPythonMarker(path))
            return "python";
        if (File.Exists(Path.Combine(path, "package.json")))
            return "node";

        return null;
    }

    internal static IReadOnlyList<ProjectStepPlan> BuildProjectStepPlans(
        string path,
        string projectType,
        IReadOnlyList<string> steps,
        string configuration)
    {
        return projectType switch
        {
            "dotnet" => BuildDotNetPlans(path, steps, configuration),
            "node" => BuildNodePlans(path, steps),
            "rust" => BuildRustPlans(steps, configuration),
            "php" => BuildPhpPlans(path, steps),
            "python" => BuildPythonPlans(path, steps),
            "go" => BuildGoPlans(steps),
            _ => throw new ProjectCheckException(
                ErrorCodes.InvalidRequest,
                "Unsupported project type.")
        };
    }

    private static CommandError? ValidateProjectCheckRequest(
        string projectType,
        IReadOnlyList<string> steps,
        string configuration,
        int timeoutSeconds,
        int maxOutputBytes)
    {
        if (!SupportedProjectTypes.Contains(projectType))
        {
            return new CommandError(
                ErrorCodes.InvalidRequest,
                "projectType must be auto, dotnet, node, rust, php, python, or go.");
        }

        if (steps.Count is < 1 or > 4 ||
            steps.Any(step => !SupportedProjectSteps.Contains(step)) ||
            steps.Distinct(StringComparer.Ordinal).Count() != steps.Count)
        {
            return new CommandError(
                ErrorCodes.InvalidRequest,
                "steps must contain between 1 and 4 unique values chosen from build, test, lint, and typecheck.");
        }

        if (!string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase))
        {
            return new CommandError(
                ErrorCodes.InvalidRequest,
                "configuration must be Debug or Release.");
        }

        if (timeoutSeconds is < 30 or > 900)
        {
            return new CommandError(
                ErrorCodes.InvalidRequest,
                "timeoutSeconds must be between 30 and 900.");
        }

        if (maxOutputBytes is < 1024 or > 4_194_304)
        {
            return new CommandError(
                ErrorCodes.InvalidRequest,
                "maxOutputBytes must be between 1024 and 4194304.");
        }

        return null;
    }

    private static string ResolveProjectType(string path, string requestedProjectType)
    {
        if (requestedProjectType == "auto")
        {
            return DetectProjectType(path) ??
                throw new ProjectCheckException(
                    ErrorCodes.ProjectTypeNotDetected,
                    "No supported project marker was found in the requested directory.");
        }

        if (!HasProjectMarker(path, requestedProjectType))
        {
            throw new ProjectCheckException(
                ErrorCodes.ProjectTypeNotDetected,
                $"The requested directory does not contain a {requestedProjectType} project marker.");
        }

        return requestedProjectType;
    }

    private static bool HasProjectMarker(string path, string projectType) =>
        projectType switch
        {
            "dotnet" => HasDotNetMarker(path),
            "node" => File.Exists(Path.Combine(path, "package.json")),
            "rust" => File.Exists(Path.Combine(path, "Cargo.toml")),
            "php" => File.Exists(Path.Combine(path, "composer.json")) ||
                File.Exists(Path.Combine(path, "artisan")),
            "python" => HasPythonMarker(path),
            "go" => File.Exists(Path.Combine(path, "go.mod")),
            _ => false
        };

    private static bool HasPythonMarker(string path) =>
        File.Exists(Path.Combine(path, "pyproject.toml")) ||
        File.Exists(Path.Combine(path, "requirements.txt")) ||
        File.Exists(Path.Combine(path, "setup.py")) ||
        File.Exists(Path.Combine(path, "setup.cfg"));

    private static bool HasDotNetMarker(string path) =>
        Directory
            .EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
            .Any(file =>
            {
                var extension = Path.GetExtension(file);
                return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase);
            });

    private static IReadOnlyList<ProjectStepPlan> BuildDotNetPlans(
        string path,
        IReadOnlyList<string> steps,
        string configuration)
    {
        var target = ResolveDotNetTarget(path);
        var plans = new List<ProjectStepPlan>(steps.Count);
        var buildIndex = IndexOfStep(steps, "build");

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            switch (step)
            {
                case "build":
                    plans.Add(CreatePlan(
                        step,
                        "dotnet",
                        "dotnet.exe",
                        ["build", target, "--nologo", "--configuration", configuration]));
                    break;

                case "test":
                {
                    var arguments = new List<string>
                    {
                        "test",
                        target,
                        "--nologo",
                        "--configuration",
                        configuration
                    };
                    if (buildIndex >= 0 && buildIndex < index)
                        arguments.Add("--no-build");

                    plans.Add(CreatePlan(step, "dotnet", "dotnet.exe", arguments));
                    break;
                }

                case "lint":
                    plans.Add(CreatePlan(
                        step,
                        "dotnet",
                        "dotnet.exe",
                        ["format", target, "--verify-no-changes", "--no-restore"]));
                    break;

                case "typecheck":
                    plans.Add(CreatePlan(
                        step,
                        "dotnet",
                        "dotnet.exe",
                        [
                            "build",
                            target,
                            "--nologo",
                            "--configuration",
                            configuration,
                            "--no-restore"
                        ]));
                    break;
            }
        }

        return plans;
    }

    private static IReadOnlyList<ProjectStepPlan> BuildNodePlans(
        string path,
        IReadOnlyList<string> steps)
    {
        var packageRoot = ReadJsonManifest(Path.Combine(path, "package.json"));
        var scripts = GetScriptNames(packageRoot);
        var packageManager = DetectNodePackageManager(path, packageRoot);
        var executable = packageManager switch
        {
            "pnpm" => "pnpm.cmd",
            "yarn" => "yarn.cmd",
            "bun" => "bun.exe",
            _ => "npm.cmd"
        };

        return steps
            .Select(step =>
            {
                var scriptName = step == "typecheck" &&
                    !scripts.Contains("typecheck") &&
                    scripts.Contains("type-check")
                        ? "type-check"
                        : step;

                return scripts.Contains(scriptName)
                    ? CreatePlan(step, packageManager, executable, ["run", scriptName])
                    : CreateSkippedPlan(
                        step,
                        packageManager,
                        $"script_not_found:{scriptName}");
            })
            .ToList();
    }

    private static IReadOnlyList<ProjectStepPlan> BuildPythonPlans(
        string path,
        IReadOnlyList<string> steps)
    {
        var plans = new List<ProjectStepPlan>(steps.Count);

        foreach (var step in steps)
        {
            switch (step)
            {
                case "build":
                    if (File.Exists(Path.Combine(path, "pyproject.toml")) ||
                        File.Exists(Path.Combine(path, "setup.py")))
                    {
                        plans.Add(CreatePlanFromCandidates(
                            step,
                            BuildPythonInterpreterCandidates(
                                path,
                                "python-build",
                                ["-m", "build", "--no-isolation"])));
                    }
                    else
                    {
                        plans.Add(CreateSkippedPlan(
                            step,
                            "python",
                            "build_manifest_not_found"));
                    }
                    break;

                case "test":
                {
                    var candidates = new List<ProjectCommandCandidate>();
                    AddProjectVenvCandidate(
                        path,
                        candidates,
                        "pytest",
                        "pytest.exe",
                        []);
                    candidates.Add(new ProjectCommandCandidate(
                        "pytest",
                        "pytest.exe",
                        []));
                    candidates.AddRange(BuildPythonInterpreterCandidates(
                        path,
                        "python-unittest",
                        ["-m", "unittest", "discover"]));
                    plans.Add(CreatePlanFromCandidates(step, candidates));
                    break;
                }

                case "lint":
                {
                    var candidates = new List<ProjectCommandCandidate>();
                    AddProjectVenvCandidate(
                        path,
                        candidates,
                        "ruff",
                        "ruff.exe",
                        ["check", "."]);
                    candidates.Add(new ProjectCommandCandidate(
                        "ruff",
                        "ruff.exe",
                        ["check", "."]));
                    AddProjectVenvCandidate(
                        path,
                        candidates,
                        "flake8",
                        "flake8.exe",
                        ["."]);
                    candidates.Add(new ProjectCommandCandidate(
                        "flake8",
                        "flake8.exe",
                        ["."]));
                    plans.Add(CreatePlanFromCandidates(step, candidates));
                    break;
                }

                case "typecheck":
                {
                    var candidates = new List<ProjectCommandCandidate>();
                    AddProjectVenvCandidate(
                        path,
                        candidates,
                        "mypy",
                        "mypy.exe",
                        ["."]);
                    candidates.Add(new ProjectCommandCandidate(
                        "mypy",
                        "mypy.exe",
                        ["."]));
                    AddProjectVenvCandidate(
                        path,
                        candidates,
                        "pyright",
                        "pyright.exe",
                        []);
                    AddProjectVenvCandidate(
                        path,
                        candidates,
                        "pyright",
                        "pyright.cmd",
                        []);
                    candidates.Add(new ProjectCommandCandidate(
                        "pyright",
                        "pyright.cmd",
                        []));
                    candidates.Add(new ProjectCommandCandidate(
                        "pyright",
                        "pyright.exe",
                        []));
                    plans.Add(CreatePlanFromCandidates(step, candidates));
                    break;
                }
            }
        }

        return plans;
    }

    private static IReadOnlyList<ProjectStepPlan> BuildGoPlans(
        IReadOnlyList<string> steps)
    {
        return steps
            .Select(step =>
            {
                var arguments = step switch
                {
                    "build" => new List<string> { "build", "./..." },
                    "test" => new List<string> { "test", "./..." },
                    "lint" => new List<string> { "vet", "./..." },
                    "typecheck" => new List<string> { "test", "-run=^$", "./..." },
                    _ => []
                };

                return CreatePlan(step, "go", "go.exe", arguments);
            })
            .ToList();
    }

    private static List<ProjectCommandCandidate> BuildPythonInterpreterCandidates(
        string path,
        string toolchain,
        IReadOnlyList<string> arguments)
    {
        var candidates = new List<ProjectCommandCandidate>();
        AddProjectVenvCandidate(
            path,
            candidates,
            toolchain,
            "python.exe",
            arguments);
        candidates.Add(new ProjectCommandCandidate(
            toolchain,
            "python.exe",
            arguments));
        candidates.Add(new ProjectCommandCandidate(
            toolchain,
            "py.exe",
            new[] { "-3" }.Concat(arguments).ToList()));
        return candidates;
    }

    private static void AddProjectVenvCandidate(
        string path,
        ICollection<ProjectCommandCandidate> candidates,
        string toolchain,
        string executableName,
        IReadOnlyList<string> arguments)
    {
        var relativePath = Path.Combine(".venv", "Scripts", executableName);
        var fullPath = Path.GetFullPath(Path.Combine(path, relativePath));
        if (IsTrustedProjectLocalExecutable(path, fullPath))
        {
            candidates.Add(new ProjectCommandCandidate(
                toolchain,
                relativePath,
                arguments));
        }
    }

    private static bool IsTrustedProjectLocalExecutable(
        string workingDirectory,
        string candidate)
    {
        try
        {
            var root = Path.GetFullPath(workingDirectory);
            var fullCandidate = Path.GetFullPath(candidate);
            if (!IsPathWithinRoot(root, fullCandidate))
                return false;

            var relativePath = Path.GetRelativePath(root, fullCandidate);
            var segments = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 3 ||
                !string.Equals(segments[0], ".venv", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(segments[1], "Scripts", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var current = root;
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                if (!File.Exists(current) && !Directory.Exists(current))
                    return false;
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    return false;
            }

            return File.Exists(fullCandidate);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            NotSupportedException or
            PathTooLongException or
            IOException or
            UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IReadOnlyList<ProjectStepPlan> BuildRustPlans(
        IReadOnlyList<string> steps,
        string configuration)
    {
        var release = string.Equals(configuration, "Release", StringComparison.Ordinal);

        return steps
            .Select(step =>
            {
                var arguments = step switch
                {
                    "build" => new List<string> { "build" },
                    "test" => new List<string> { "test" },
                    "lint" => new List<string>
                    {
                        "clippy",
                        "--all-targets",
                        "--all-features",
                        "--",
                        "-D",
                        "warnings"
                    },
                    "typecheck" => new List<string> { "check" },
                    _ => []
                };

                if (release && step is ("build" or "test" or "typecheck"))
                    arguments.Add("--release");

                return CreatePlan(step, "cargo", "cargo.exe", arguments);
            })
            .ToList();
    }

    private static IReadOnlyList<ProjectStepPlan> BuildPhpPlans(
        string path,
        IReadOnlyList<string> steps)
    {
        var composerScripts = File.Exists(Path.Combine(path, "composer.json"))
            ? GetScriptNames(ReadJsonManifest(Path.Combine(path, "composer.json")))
            : new HashSet<string>(StringComparer.Ordinal);

        JsonElement? packageRoot = File.Exists(Path.Combine(path, "package.json"))
            ? ReadJsonManifest(Path.Combine(path, "package.json"))
            : null;
        var nodeScripts = packageRoot.HasValue
            ? GetScriptNames(packageRoot.Value)
            : new HashSet<string>(StringComparer.Ordinal);
        var nodeManager = packageRoot.HasValue
            ? DetectNodePackageManager(path, packageRoot.Value)
            : "npm";
        var nodeExecutable = nodeManager switch
        {
            "pnpm" => "pnpm.cmd",
            "yarn" => "yarn.cmd",
            "bun" => "bun.exe",
            _ => "npm.cmd"
        };

        var plans = new List<ProjectStepPlan>(steps.Count);
        foreach (var step in steps)
        {
            switch (step)
            {
                case "build":
                    if (nodeScripts.Contains("build"))
                        plans.Add(CreatePlan(step, nodeManager, nodeExecutable, ["run", "build"]));
                    else if (composerScripts.Contains("build"))
                        plans.Add(CreateComposerPlan(step, "build"));
                    else
                        plans.Add(CreateSkippedPlan(step, "php", "build_step_not_found"));
                    break;

                case "test":
                    if (File.Exists(Path.Combine(path, "artisan")))
                        plans.Add(CreatePlan(
                            step,
                            "php-artisan",
                            "php.exe",
                            ["artisan", "test", "--no-interaction"]));
                    else if (File.Exists(Path.Combine(path, "vendor", "phpunit", "phpunit", "phpunit")))
                        plans.Add(CreatePlan(
                            step,
                            "phpunit",
                            "php.exe",
                            ["vendor/phpunit/phpunit/phpunit"]));
                    else if (composerScripts.Contains("test"))
                        plans.Add(CreateComposerPlan(step, "test"));
                    else
                        plans.Add(CreateSkippedPlan(step, "php", "test_step_not_found"));
                    break;

                case "lint":
                    if (File.Exists(Path.Combine(path, "vendor", "laravel", "pint", "builds", "pint")))
                        plans.Add(CreatePlan(
                            step,
                            "laravel-pint",
                            "php.exe",
                            ["vendor/laravel/pint/builds/pint", "--test"]));
                    else if (composerScripts.Contains("lint"))
                        plans.Add(CreateComposerPlan(step, "lint"));
                    else if (nodeScripts.Contains("lint"))
                        plans.Add(CreatePlan(step, nodeManager, nodeExecutable, ["run", "lint"]));
                    else
                        plans.Add(CreateSkippedPlan(step, "php", "lint_step_not_found"));
                    break;

                case "typecheck":
                    if (File.Exists(Path.Combine(path, "vendor", "phpstan", "phpstan", "phpstan")))
                        plans.Add(CreatePlan(
                            step,
                            "phpstan",
                            "php.exe",
                            ["vendor/phpstan/phpstan/phpstan", "analyse", "--no-progress"]));
                    else if (composerScripts.Contains("typecheck"))
                        plans.Add(CreateComposerPlan(step, "typecheck"));
                    else if (nodeScripts.Contains("typecheck"))
                        plans.Add(CreatePlan(step, nodeManager, nodeExecutable, ["run", "typecheck"]));
                    else
                        plans.Add(CreateSkippedPlan(step, "php", "typecheck_step_not_found"));
                    break;
            }
        }

        return plans;
    }

    private static ProjectStepPlan CreateComposerPlan(string step, string scriptName) =>
        CreatePlan(
            step,
            "composer",
            "composer.bat",
            ["run-script", "--no-interaction", scriptName]);

    private static string ResolveDotNetTarget(string path)
    {
        var solutions = Directory
            .EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
            .Where(file =>
            {
                var extension = Path.GetExtension(file);
                return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (solutions.Count > 1)
        {
            throw new ProjectCheckException(
                ErrorCodes.ProjectManifestInvalid,
                "Multiple solution files were found. Use a directory containing one solution.");
        }

        if (solutions.Count == 1)
            return Path.GetFileName(solutions[0]);

        var projects = Directory
            .EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
            .Where(file =>
            {
                var extension = Path.GetExtension(file);
                return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (projects.Count != 1)
        {
            throw new ProjectCheckException(
                ErrorCodes.ProjectManifestInvalid,
                "The directory must contain exactly one solution or project file.");
        }

        return Path.GetFileName(projects[0]);
    }

    private static JsonElement ReadJsonManifest(string manifestPath)
    {
        try
        {
            var info = new FileInfo(manifestPath);
            if (!info.Exists)
            {
                throw new ProjectCheckException(
                    ErrorCodes.ProjectManifestInvalid,
                    $"Required project manifest '{info.Name}' was not found.");
            }

            if (info.Length > MaxProjectManifestBytes)
            {
                throw new ProjectCheckException(
                    ErrorCodes.ProjectManifestInvalid,
                    $"Project manifest '{info.Name}' exceeds the 1 MiB limit.");
            }

            var text = ProjectManifestEncoding.GetString(File.ReadAllBytes(manifestPath));
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text[1..];

            using var document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ProjectCheckException(
                    ErrorCodes.ProjectManifestInvalid,
                    $"Project manifest '{info.Name}' must contain a JSON object.");
            }

            return document.RootElement.Clone();
        }
        catch (ProjectCheckException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            DecoderFallbackException or
            JsonException)
        {
            throw new ProjectCheckException(
                ErrorCodes.ProjectManifestInvalid,
                "A project manifest could not be read as valid UTF-8 JSON.");
        }
    }

    private static HashSet<string> GetScriptNames(JsonElement root)
    {
        var scripts = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("scripts", out var scriptsElement) ||
            scriptsElement.ValueKind != JsonValueKind.Object)
        {
            return scripts;
        }

        foreach (var property in scriptsElement.EnumerateObject())
            scripts.Add(property.Name);

        return scripts;
    }

    private static string DetectNodePackageManager(string path, JsonElement packageRoot)
    {
        if (File.Exists(Path.Combine(path, "pnpm-lock.yaml")))
            return "pnpm";
        if (File.Exists(Path.Combine(path, "yarn.lock")))
            return "yarn";
        if (File.Exists(Path.Combine(path, "bun.lock")) ||
            File.Exists(Path.Combine(path, "bun.lockb")))
        {
            return "bun";
        }
        if (File.Exists(Path.Combine(path, "package-lock.json")))
            return "npm";

        if (packageRoot.TryGetProperty("packageManager", out var packageManager) &&
            packageManager.ValueKind == JsonValueKind.String)
        {
            var value = packageManager.GetString() ?? string.Empty;
            var separator = value.IndexOf('@');
            var name = separator >= 0 ? value[..separator] : value;
            if (name is "pnpm" or "yarn" or "bun" or "npm")
                return name;
        }

        return "npm";
    }

    private async Task<ProjectVerifyStepResult> RunProjectStepAsync(
        string workingDirectory,
        ProjectStepPlan plan,
        int maxOutputBytes,
        CancellationToken linkedCancellationToken,
        CancellationToken callerCancellationToken,
        CancellationToken timeoutCancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var resolvedStep = ResolveProjectStep(plan, workingDirectory);
        if (resolvedStep is null)
            return CreateToolMissingStep(plan, stopwatch.ElapsedMilliseconds, maxOutputBytes);

        var selectedPlan = resolvedStep.Plan;
        var startInfo = CreateProjectProcessStartInfo(
            workingDirectory,
            selectedPlan,
            resolvedStep.Executable);
        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
                return CreateToolMissingStep(plan, stopwatch.ElapsedMilliseconds, maxOutputBytes);

            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(
                ex,
                "Unable to start project toolchain {Toolchain}",
                selectedPlan.Toolchain);
            return CreateToolMissingStep(plan, stopwatch.ElapsedMilliseconds, maxOutputBytes);
        }

        var stdoutTask = ReadProjectOutputAsync(process.StandardOutput.BaseStream, maxOutputBytes);
        var stderrTask = ReadProjectOutputAsync(process.StandardError.BaseStream, maxOutputBytes);
        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(linkedCancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProjectProcess(process);
            await WaitForProjectProcessExitAsync(process);

            if (callerCancellationToken.IsCancellationRequested)
                throw;

            timedOut = timeoutCancellationToken.IsCancellationRequested;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        stopwatch.Stop();

        var combinedOutput = CombineProjectOutput(stdout.Bytes, stderr.Bytes);
        var boundedOutput = TruncateProjectUtf8(combinedOutput, maxOutputBytes);
        var fullOutputBytes = ProjectOutputEncoding.GetByteCount(combinedOutput);
        int? exitCode = timedOut || !process.HasExited
            ? null
            : process.ExitCode;

        return new ProjectVerifyStepResult
        {
            Name = selectedPlan.Name,
            Toolchain = selectedPlan.Toolchain,
            DisplayCommand = selectedPlan.DisplayCommand,
            Executed = true,
            Success = exitCode == 0,
            Skipped = false,
            ExitCode = exitCode,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Output = boundedOutput.Text,
            Truncated = stdout.Truncated ||
                stderr.Truncated ||
                boundedOutput.Bytes < fullOutputBytes,
            TimedOut = timedOut
        };
    }

    private static ProcessStartInfo CreateProjectProcessStartInfo(
        string workingDirectory,
        ProjectStepPlan plan,
        string executable)
    {
        var isCommandScript =
            executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (isCommandScript)
        {
            startInfo.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/v:off");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(BuildTrustedCommandScriptInvocation(
                executable,
                plan.Arguments));
        }
        else
        {
            startInfo.FileName = executable;
            foreach (var argument in plan.Arguments)
                startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["CI"] = "true";
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["TERM"] = "dumb";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["NPM_CONFIG_COLOR"] = "false";
        startInfo.Environment["NPM_CONFIG_FUND"] = "false";
        startInfo.Environment["NPM_CONFIG_AUDIT"] = "false";
        startInfo.Environment["NPM_CONFIG_UPDATE_NOTIFIER"] = "false";
        startInfo.Environment["CARGO_TERM_COLOR"] = "never";
        startInfo.Environment["COMPOSER_NO_INTERACTION"] = "1";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PIP_NO_INPUT"] = "1";
        startInfo.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        startInfo.Environment["GOTOOLCHAIN"] = "local";
        startInfo.Environment["GOPROXY"] = "off";
        startInfo.Environment["GOSUMDB"] = "off";

        return startInfo;
    }

    private static ResolvedProjectStep? ResolveProjectStep(
        ProjectStepPlan plan,
        string workingDirectory)
    {
        var candidates = new[]
        {
            new ProjectCommandCandidate(
                plan.Toolchain,
                plan.Executable,
                plan.Arguments)
        }.Concat(plan.Alternatives);

        foreach (var candidate in candidates)
        {
            var executable = ResolveToolExecutable(
                candidate.Executable,
                workingDirectory);
            if (executable is null)
                continue;

            return new ResolvedProjectStep(
                plan with
                {
                    Toolchain = candidate.Toolchain,
                    Executable = candidate.Executable,
                    Arguments = candidate.Arguments,
                    Alternatives = []
                },
                executable);
        }

        return null;
    }

    internal static string? ResolveToolExecutable(
        string executable,
        string workingDirectory)
    {
        if (executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
        {
            try
            {
                var localCandidate = Path.GetFullPath(Path.Combine(
                    workingDirectory,
                    executable));
                return IsTrustedProjectLocalExecutable(
                    workingDirectory,
                    localCandidate)
                        ? localCandidate
                        : null;
            }
            catch (Exception ex) when (
                ex is ArgumentException or
                NotSupportedException or
                PathTooLongException or
                IOException or
                UnauthorizedAccessException)
            {
                return null;
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        foreach (var rawDirectory in pathValue.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var directory = Environment
                    .ExpandEnvironmentVariables(rawDirectory.Trim().Trim('"'));
                if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
                    continue;

                var candidate = Path.GetFullPath(Path.Combine(directory, executable));
                if (IsPathWithinRoot(workingDirectory, candidate))
                    continue;

                var info = new FileInfo(candidate);
                if (info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return candidate;
            }
            catch (Exception ex) when (
                ex is ArgumentException or
                NotSupportedException or
                PathTooLongException or
                IOException or
                UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    internal static string BuildTrustedCommandScriptInvocation(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var tokens = new List<string> { executable };
        tokens.AddRange(arguments);

        foreach (var token in tokens)
        {
            if (token.Any(character => character is '\r' or '\n' or '"' or '%' or '!'))
                throw new InvalidOperationException("Unsafe command-script token was generated.");
        }

        return "\"\"" +
            string.Join("\" \"", tokens) +
            "\"\"";
    }

    private static async Task<ProjectBoundedOutput> ReadProjectOutputAsync(
        Stream stream,
        int maxBytes)
    {
        using var destination = new MemoryStream(Math.Min(maxBytes, 65_536));
        var buffer = new byte[8192];
        var truncated = false;
        int read;

        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            var remaining = maxBytes - (int)destination.Length;
            if (remaining > 0)
                destination.Write(buffer, 0, Math.Min(remaining, read));
            if (read > remaining)
                truncated = true;
        }

        return new ProjectBoundedOutput(destination.ToArray(), truncated);
    }

    private static string CombineProjectOutput(byte[] stdout, byte[] stderr)
    {
        var stdoutText = ProjectOutputEncoding.GetString(stdout);
        var stderrText = ProjectOutputEncoding.GetString(stderr);

        if (stderrText.Length == 0)
            return stdoutText;
        if (stdoutText.Length == 0)
            return "[stderr]\n" + stderrText;

        return stdoutText.TrimEnd('\r', '\n') +
            "\n[stderr]\n" +
            stderrText;
    }

    private static (string Text, int Bytes) TruncateProjectUtf8(
        string value,
        int maxBytes)
    {
        if (maxBytes <= 0 || value.Length == 0)
            return (string.Empty, 0);

        var builder = new StringBuilder(Math.Min(value.Length, maxBytes));
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
                break;

            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }

        return (builder.ToString(), bytes);
    }

    private static void TryKillProjectProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static async Task WaitForProjectProcessExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch
        {
        }
    }

    private static ProjectVerifyStepResult CreateToolMissingStep(
        ProjectStepPlan plan,
        long durationMs,
        int maxOutputBytes)
    {
        var toolchains = new[] { plan.Toolchain }
            .Concat(plan.Alternatives.Select(candidate => candidate.Toolchain))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var message = $"Project toolchains '{string.Join(", ", toolchains)}' are not available on the Windows agent.";
        var bounded = TruncateProjectUtf8(message, maxOutputBytes);

        return new ProjectVerifyStepResult
        {
            Name = plan.Name,
            Toolchain = plan.Toolchain,
            DisplayCommand = plan.DisplayCommand,
            Executed = false,
            Success = false,
            Skipped = false,
            ExitCode = null,
            DurationMs = durationMs,
            Output = bounded.Text,
            Truncated = bounded.Bytes < ProjectOutputEncoding.GetByteCount(message),
            TimedOut = false
        };
    }

    private static ProjectVerifyStepResult CreateSkippedStep(
        ProjectStepPlan plan,
        string reason,
        bool timedOut) => new()
    {
        Name = plan.Name,
        Toolchain = plan.Toolchain,
        DisplayCommand = plan.DisplayCommand,
        Executed = false,
        Success = false,
        Skipped = true,
        SkipReason = reason,
        ExitCode = null,
        DurationMs = 0,
        Output = string.Empty,
        Truncated = false,
        TimedOut = timedOut
    };

    private static ProjectStepPlan CreatePlan(
        string name,
        string toolchain,
        string executable,
        IReadOnlyList<string> arguments) => new(
            Name: name,
            Toolchain: toolchain,
            Executable: executable,
            Arguments: arguments,
            Supported: true,
            SkipReason: null);

    private static ProjectStepPlan CreatePlanFromCandidates(
        string name,
        IReadOnlyList<ProjectCommandCandidate> candidates)
    {
        if (candidates.Count == 0)
            throw new InvalidOperationException("At least one project command candidate is required.");

        var primary = candidates[0];
        return new ProjectStepPlan(
            Name: name,
            Toolchain: primary.Toolchain,
            Executable: primary.Executable,
            Arguments: primary.Arguments,
            Supported: true,
            SkipReason: null)
        {
            Alternatives = candidates.Skip(1).ToList()
        };
    }

    private static ProjectStepPlan CreateSkippedPlan(
        string name,
        string toolchain,
        string reason) => new(
            Name: name,
            Toolchain: toolchain,
            Executable: string.Empty,
            Arguments: [],
            Supported: false,
            SkipReason: reason);

    private static int IndexOfStep(IReadOnlyList<string> steps, string target)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            if (string.Equals(steps[index], target, StringComparison.Ordinal))
                return index;
        }

        return -1;
    }

    private static string FormatProjectCommand(
        string executable,
        IReadOnlyList<string> arguments)
    {
        static string QuoteForDisplay(string value) =>
            value.Any(char.IsWhiteSpace)
                ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
                : value;

        return string.Join(
            " ",
            new[] { executable }
                .Concat(arguments)
                .Select(QuoteForDisplay));
    }

    private static CommandResult<ProjectVerifyResult> ProjectCheckFailure(
        Guid commandId,
        string code,
        string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };

    internal sealed record ProjectCommandCandidate(
        string Toolchain,
        string Executable,
        IReadOnlyList<string> Arguments);

    internal sealed record ProjectStepPlan(
        string Name,
        string Toolchain,
        string Executable,
        IReadOnlyList<string> Arguments,
        bool Supported,
        string? SkipReason)
    {
        public IReadOnlyList<ProjectCommandCandidate> Alternatives { get; init; } = [];

        public string DisplayCommand =>
            Supported
                ? FormatProjectCommand(Executable, Arguments)
                : string.Empty;
    }

    private sealed record ResolvedProjectStep(
        ProjectStepPlan Plan,
        string Executable);

    private sealed record ProjectBoundedOutput(byte[] Bytes, bool Truncated);

    private sealed class ProjectCheckException : Exception
    {
        public ProjectCheckException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
