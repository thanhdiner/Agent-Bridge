using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.Gateway.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalMcp.Gateway.Mcp;

[McpServerToolType]
public sealed partial class DeveloperWorkflowTools
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IExternalMcpRouter _externalRouter;
    private readonly IAuthorizationService _authorizationService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<DeveloperWorkflowTools> _logger;

    public DeveloperWorkflowTools(
        ICommandDispatcher dispatcher,
        IExternalMcpRouter externalRouter,
        IAuthorizationService authorizationService,
        ILogger<DeveloperWorkflowTools> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dispatcher = dispatcher;
        _externalRouter = externalRouter;
        _authorizationService = authorizationService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(
        Name = "extension_dev_workflow",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true),
     Description("Builds a browser extension, attempts to reload the currently selected extension context, optionally opens a test URL, and collects console/network diagnostics in one workflow. Requires dev:execute scope. The selected Chrome page may be reloaded or changed.")]
    public async Task<CallToolResult> ExtensionDevWorkflowAsync(
        [Description("Optional target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Absolute browser-extension project directory")] string path,
        [Description("package.json script to run (default: build)")] string packageScript = "build",
        [Description("Attempt chrome.runtime.reload() in the currently selected page before opening testUrl (default: true)")] bool reloadSelectedExtension = true,
        [Description("Optional absolute http/https URL to open after a successful build")] string? testUrl = null,
        [Description("Collect console messages after opening the test page (default: true)")] bool collectConsole = true,
        [Description("Collect network requests after opening the test page (default: true)")] bool collectNetwork = true,
        [Description("Build timeout in seconds (default: 300, hard limit: 900)")] int timeoutSeconds = 300)
    {
        var authError = await RequireDevExecuteAsync();
        if (authError is not null)
            return authError;
        if (string.IsNullOrWhiteSpace(path))
            return Error("INVALID_REQUEST", "path is required.");
        if (!ValidSimpleName(packageScript, 100))
            return Error("INVALID_REQUEST", "packageScript must contain only letters, numbers, '.', '_', ':', or '-'.");
        if (timeoutSeconds is < 30 or > 900)
            return Error("INVALID_REQUEST", "timeoutSeconds must be between 30 and 900.");
        if (testUrl is not null && !IsSafeWebUrl(testUrl))
            return Error("INVALID_REQUEST", "testUrl must be an absolute http or https URL.");

        var build = await RunPowerShellAsync(
            deviceId,
            path,
            DeveloperWorkflowScripts.BuildExtension(path, packageScript),
            timeoutSeconds,
            4_194_304);
        if (!build.Success || build.Data is null)
            return FromCommandFailure(build);

        var buildData = ParseLastJson(build.Data.Stdout);
        var buildSucceeded = buildData is { ValueKind: JsonValueKind.Object }
            && buildData.Value.TryGetProperty("success", out var successProperty)
            && successProperty.ValueKind == JsonValueKind.True;

        object? reload = null;
        object? openedPage = null;
        object? console = null;
        object? network = null;

        if (buildSucceeded && reloadSelectedExtension)
        {
            reload = DescribeExternal(await CallChromeAsync(
                "evaluate_script",
                new
                {
                    function = "async () => { const runtime = globalThis.chrome?.runtime; if (!runtime?.id || typeof runtime.reload !== 'function') return { reloaded: false, reason: 'selected_page_is_not_an_extension_context', url: location.href }; const id = runtime.id; const url = location.href; setTimeout(() => runtime.reload(), 0); return { reloaded: true, extensionId: id, url }; }"
                }));
        }

        if (buildSucceeded && testUrl is not null)
        {
            openedPage = DescribeExternal(await CallChromeAsync(
                "new_page",
                new { url = testUrl, background = false, timeout = 30_000 }));
        }

        if (buildSucceeded && collectConsole)
        {
            console = DescribeExternal(await CallChromeAsync(
                "list_console_messages",
                new { pageSize = 200, pageIdx = 0, includePreservedMessages = true }));
        }

        if (buildSucceeded && collectNetwork)
        {
            network = DescribeExternal(await CallChromeAsync(
                "list_network_requests",
                new { pageSize = 300, pageIdx = 0, includePreservedRequests = true }));
        }

        return Json(new
        {
            build = new
            {
                execution = build.Data,
                data = buildData
            },
            browser = new
            {
                reload,
                openedPage,
                console,
                network
            }
        });
    }

    [McpServerTool(
        Name = "browser_extension_inspect",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true),
     Description("Inspects the selected Chrome page or extension context: runtime manifest/id, granted permissions, extension storage when accessible, service-worker registrations, console messages, network requests, and open pages. Requires dev:execute scope and can access current Chrome profile data.")]
    public async Task<CallToolResult> BrowserExtensionInspectAsync(
        [Description("Optional page id to select before inspection. Obtain it from the returned page list or chrome-devtools.list_pages.")] int? pageId = null,
        [Description("Optional service-worker id used to filter console messages")] string? serviceWorkerId = null,
        [Description("Maximum console messages (default: 200, hard limit: 1000)")] int maxConsoleMessages = 200,
        [Description("Maximum network requests (default: 300, hard limit: 1000)")] int maxNetworkRequests = 300,
        [Description("Include extension storage values when the selected context permits it (default: true)")] bool includeStorage = true)
    {
        var authError = await RequireDevExecuteAsync();
        if (authError is not null)
            return authError;
        if (pageId is <= 0)
            return Error("INVALID_REQUEST", "pageId must be greater than zero when provided.");
        if (maxConsoleMessages is < 1 or > 1_000 || maxNetworkRequests is < 1 or > 1_000)
            return Error("INVALID_REQUEST", "message/request limits must be between 1 and 1000.");
        if (serviceWorkerId is not null && (serviceWorkerId.Length > 300 || serviceWorkerId.Any(char.IsControl)))
            return Error("INVALID_REQUEST", "serviceWorkerId is invalid.");

        object? selection = null;
        if (pageId.HasValue)
        {
            selection = DescribeExternal(await CallChromeAsync(
                "select_page",
                new { pageId = pageId.Value, bringToFront = false }));
        }

        var storageFlag = includeStorage ? "true" : "false";
        var inspectFunction = $$"""
async () => {
  const result = {
    url: location.href,
    title: document.title,
    extensionContext: Boolean(globalThis.chrome?.runtime?.id),
    extensionId: globalThis.chrome?.runtime?.id ?? null,
    manifest: null,
    permissions: null,
    storage: null,
    serviceWorkers: [],
    errors: []
  };
  try { result.manifest = globalThis.chrome?.runtime?.getManifest?.() ?? null; } catch (error) { result.errors.push(`manifest: ${error}`); }
  try { result.permissions = globalThis.chrome?.permissions?.getAll ? await globalThis.chrome.permissions.getAll() : null; } catch (error) { result.errors.push(`permissions: ${error}`); }
  if ({{storageFlag}}) {
    result.storage = {};
    for (const area of ['local', 'session', 'sync', 'managed']) {
      try {
        const target = globalThis.chrome?.storage?.[area];
        result.storage[area] = target?.get ? await target.get(null) : null;
      } catch (error) {
        result.storage[area] = { error: String(error) };
      }
    }
  }
  try {
    const registrations = navigator.serviceWorker?.getRegistrations ? await navigator.serviceWorker.getRegistrations() : [];
    result.serviceWorkers = registrations.map(registration => ({
      scope: registration.scope,
      active: registration.active?.scriptURL ?? null,
      waiting: registration.waiting?.scriptURL ?? null,
      installing: registration.installing?.scriptURL ?? null
    }));
  } catch (error) { result.errors.push(`serviceWorkers: ${error}`); }
  return result;
}
""";

        var runtime = DescribeExternal(await CallChromeAsync(
            "evaluate_script",
            new { function = inspectFunction }));
        var pages = DescribeExternal(await CallChromeAsync("list_pages", new { }));

        var consoleArguments = new Dictionary<string, object?>
        {
            ["pageSize"] = maxConsoleMessages,
            ["pageIdx"] = 0,
            ["includePreservedMessages"] = true
        };
        if (!string.IsNullOrWhiteSpace(serviceWorkerId))
            consoleArguments["serviceWorkerId"] = serviceWorkerId;

        var console = DescribeExternal(await CallChromeAsync("list_console_messages", consoleArguments));
        var network = DescribeExternal(await CallChromeAsync(
            "list_network_requests",
            new { pageSize = maxNetworkRequests, pageIdx = 0, includePreservedRequests = true }));

        return Json(new { selection, runtime, pages, console, network });
    }

    [McpServerTool(
        Name = "dom_event_trace",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true),
     Description("Temporarily traces DOM mutations, event-listener registrations, timers, intervals, and requestAnimationFrame calls on the selected Chrome page, then restores patched browser APIs and returns a bounded summary. Requires dev:execute scope.")]
    public async Task<CallToolResult> DomEventTraceAsync(
        [Description("Optional page id to select before tracing")] int? pageId = null,
        [Description("Trace duration in milliseconds (default: 3000, range: 250-15000)")] int durationMs = 3_000,
        [Description("Maximum samples retained for each category (default: 100, hard limit: 500)")] int maxSamples = 100,
        [Description("Collect console messages after the trace (default: true)")] bool collectConsole = true)
    {
        var authError = await RequireDevExecuteAsync();
        if (authError is not null)
            return authError;
        if (pageId is <= 0)
            return Error("INVALID_REQUEST", "pageId must be greater than zero when provided.");
        if (durationMs is < 250 or > 15_000)
            return Error("INVALID_REQUEST", "durationMs must be between 250 and 15000.");
        if (maxSamples is < 1 or > 500)
            return Error("INVALID_REQUEST", "maxSamples must be between 1 and 500.");

        object? selection = null;
        if (pageId.HasValue)
        {
            selection = DescribeExternal(await CallChromeAsync(
                "select_page",
                new { pageId = pageId.Value, bringToFront = false }));
        }

        var start = await CallChromeAsync(
            "evaluate_script",
            new { function = BuildDomTraceStartFunction(maxSamples) });
        if (start.IsError == true)
            return start;

        await Task.Delay(durationMs, RequestToken());

        var stop = await CallChromeAsync(
            "evaluate_script",
            new
            {
                function = "() => globalThis.__agentBridgeDomTrace?.stop?.() ?? { active: false, reason: 'trace_not_found' }"
            });

        object? console = null;
        if (collectConsole)
        {
            console = DescribeExternal(await CallChromeAsync(
                "list_console_messages",
                new { pageSize = 200, pageIdx = 0, includePreservedMessages = true }));
        }

        return Json(new
        {
            selection,
            durationMs,
            start = DescribeExternal(start),
            trace = DescribeExternal(stop),
            console
        });
    }

    [McpServerTool(
        Name = "process_tree_supervisor",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false),
     Description("Inspects Windows process ancestry, command lines, executable paths, and listening ports, optionally filtered by repository path or process name. With action=kill, terminates one guarded process tree. Requires dev:execute scope.")]
    public async Task<CallToolResult> ProcessTreeSupervisorAsync(
        [Description("Optional target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Action: inspect or kill (default: inspect)")] string action = "inspect",
        [Description("Working directory used for path authorization and script execution. Required for inspect.")] string? path = null,
        [Description("Optional repository/root path filter; defaults to path when omitted")] string? rootPath = null,
        [Description("Optional case-insensitive process-name or command-line filter")] string? nameContains = null,
        [Description("Include listening TCP ports during inspect (default: true)")] bool includePorts = true,
        [Description("Maximum processes returned during inspect (default: 300, hard limit: 1000)")] int maxResults = 300,
        [Description("Exact process id to terminate when action=kill")] int? processId = null,
        [Description("Expected process name guard when action=kill")] string? expectedProcessName = null,
        [Description("Kill complete descendant process tree when action=kill (default: true)")] bool entireProcessTree = true,
        [Description("Kill wait timeout in milliseconds (default: 5000, hard limit: 300000)")] int timeoutMs = 5_000)
    {
        var authError = await RequireDevExecuteAsync();
        if (authError is not null)
            return authError;

        var normalizedAction = action.Trim().ToLowerInvariant();
        if (normalizedAction == "kill")
        {
            if (processId is null or <= 0)
                return Error("INVALID_REQUEST", "processId must be greater than zero for action=kill.");
            if (string.IsNullOrWhiteSpace(expectedProcessName))
                return Error("INVALID_REQUEST", "expectedProcessName is required for action=kill.");
            if (timeoutMs is < 1 or > 300_000)
                return Error("INVALID_REQUEST", "timeoutMs must be between 1 and 300000.");

            var kill = await _dispatcher.SendAsync<ProcessKillResult>(
                new ProcessKillCommand
                {
                    CommandId = Guid.NewGuid(),
                    DeviceId = deviceId ?? string.Empty,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ProcessId = processId.Value,
                    ExpectedProcessName = expectedProcessName,
                    EntireProcessTree = entireProcessTree,
                    TimeoutMs = timeoutMs
                },
                RequestToken());
            return kill.Success && kill.Data is not null ? Json(kill.Data) : FromCommandFailure(kill);
        }

        if (normalizedAction != "inspect")
            return Error("INVALID_REQUEST", "action must be inspect or kill.");
        if (string.IsNullOrWhiteSpace(path))
            return Error("INVALID_REQUEST", "path is required for action=inspect.");
        if (maxResults is < 1 or > 1_000)
            return Error("INVALID_REQUEST", "maxResults must be between 1 and 1000.");
        if (nameContains is not null && (nameContains.Length > 260 || nameContains.Any(char.IsControl)))
            return Error("INVALID_REQUEST", "nameContains is invalid.");

        var inspect = await RunPowerShellAsync(
            deviceId,
            path,
            DeveloperWorkflowScripts.InspectProcessTree(rootPath ?? path, nameContains, includePorts, maxResults),
            120,
            4_194_304);
        if (!inspect.Success || inspect.Data is null)
            return FromCommandFailure(inspect);

        return Json(new
        {
            execution = inspect.Data,
            data = ParseLastJson(inspect.Data.Stdout)
        });
    }

    [McpServerTool(
        Name = "dev_session_run",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true),
     Description("Initializes, starts, polls, or stops a repository dev-session profile. Profiles live in .agentbridge/dev-sessions.json and may launch multiple project commands with separate logs. Requires dev:execute scope. Start is limited to 900 seconds by the current agent session guardrail.")]
    public async Task<CallToolResult> DevSessionRunAsync(
        [Description("Optional target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Action: init, start, status, or stop")] string action,
        [Description("Absolute repository path. Required for init/start.")] string? path = null,
        [Description("Relative config path under the repository (default: .agentbridge/dev-sessions.json)")] string configRelativePath = ".agentbridge/dev-sessions.json",
        [Description("Profile name for start (default: default)")] string profileName = "default",
        [Description("Session id returned by start. Required for status/stop.")] string? sessionId = null,
        [Description("Incremental stdout offset for status (default: 0)")] long stdoutOffset = 0,
        [Description("Incremental stderr offset for status (default: 0)")] long stderrOffset = 0,
        [Description("Maximum output bytes returned by status (default: 262144, hard limit: 262144)")] int maxOutputBytes = 262_144,
        [Description("Maximum session runtime in seconds for start (default: 900, hard limit: 900)")] int timeoutSeconds = 900)
    {
        var authError = await RequireDevExecuteAsync();
        if (authError is not null)
            return authError;
        if (!ValidRelativePath(configRelativePath))
            return Error("INVALID_REQUEST", "configRelativePath must be a safe relative path inside the repository.");
        if (!ValidSimpleName(profileName, 100))
            return Error("INVALID_REQUEST", "profileName contains unsupported characters.");

        var normalizedAction = action.Trim().ToLowerInvariant();
        if (normalizedAction is "status" or "stop")
        {
            if (!Guid.TryParse(sessionId, out var parsedSessionId))
                return Error("INVALID_REQUEST", "A valid sessionId is required for status/stop.");

            if (normalizedAction == "stop")
            {
                var cancel = await _dispatcher.SendAsync<PowerShellSessionResult>(
                    new PowerShellCancelCommand
                    {
                        CommandId = Guid.NewGuid(),
                        DeviceId = deviceId ?? string.Empty,
                        CreatedAt = DateTimeOffset.UtcNow,
                        SessionId = parsedSessionId
                    },
                    RequestToken());
                return cancel.Success && cancel.Data is not null ? Json(cancel.Data) : FromCommandFailure(cancel);
            }

            if (stdoutOffset < 0 || stderrOffset < 0)
                return Error("INVALID_REQUEST", "stdoutOffset and stderrOffset must be non-negative.");
            if (maxOutputBytes is < 4 or > 262_144)
                return Error("INVALID_REQUEST", "maxOutputBytes must be between 4 and 262144.");

            var status = await _dispatcher.SendAsync<PowerShellSessionResult>(
                new PowerShellStatusCommand
                {
                    CommandId = Guid.NewGuid(),
                    DeviceId = deviceId ?? string.Empty,
                    CreatedAt = DateTimeOffset.UtcNow,
                    SessionId = parsedSessionId,
                    StdoutOffset = stdoutOffset,
                    StderrOffset = stderrOffset,
                    MaxOutputBytes = maxOutputBytes
                },
                RequestToken());
            return status.Success && status.Data is not null ? Json(status.Data) : FromCommandFailure(status);
        }

        if (normalizedAction is not ("init" or "start"))
            return Error("INVALID_REQUEST", "action must be init, start, status, or stop.");
        if (string.IsNullOrWhiteSpace(path))
            return Error("INVALID_REQUEST", "path is required for init/start.");

        if (normalizedAction == "init")
        {
            var init = await RunPowerShellAsync(
                deviceId,
                path,
                DeveloperWorkflowScripts.InitializeDevSessions(path, configRelativePath),
                60,
                262_144);
            if (!init.Success || init.Data is null)
                return FromCommandFailure(init);
            return Json(new { execution = init.Data, data = ParseLastJson(init.Data.Stdout) });
        }

        if (timeoutSeconds is < 30 or > 900)
            return Error("INVALID_REQUEST", "timeoutSeconds must be between 30 and 900.");

        var start = await _dispatcher.SendAsync<PowerShellStartResult>(
            new PowerShellStartCommand
            {
                CommandId = Guid.NewGuid(),
                DeviceId = deviceId ?? string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
                WorkingDirectory = path,
                Script = DeveloperWorkflowScripts.StartDevSession(path, configRelativePath, profileName),
                Visible = false,
                Elevated = false,
                TimeoutSeconds = timeoutSeconds,
                MaxOutputBytes = 1_048_576
            },
            RequestToken());
        return start.Success && start.Data is not null ? Json(start.Data) : FromCommandFailure(start);
    }

    [McpServerTool(
        Name = "visual_regression_compare",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true),
     Description("Captures a named Chrome-page baseline or compares the current capture against an existing baseline. Writes baseline/current/diff PNG files under .agentbridge/visual-regression and returns pixel-difference metrics. Requires dev:execute scope and access to the current Chrome profile.")]
    public async Task<CallToolResult> VisualRegressionCompareAsync(
        [Description("Optional target device id used for filesystem preparation/comparison. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Absolute repository/project directory used to store captures")] string path,
        [Description("Action: baseline or compare")] string action,
        [Description("Stable capture name containing letters, numbers, '.', '_', or '-'")] string name,
        [Description("Optional Chrome page id to select before capture")] int? pageId = null,
        [Description("Capture the full scrollable page instead of viewport (default: false)")] bool fullPage = false,
        [Description("Per-channel pixel delta ignored as noise (default: 8, range: 0-255)")] int channelThreshold = 8)
    {
        var authError = await RequireDevExecuteAsync();
        if (authError is not null)
            return authError;
        if (string.IsNullOrWhiteSpace(path))
            return Error("INVALID_REQUEST", "path is required.");
        if (!ValidCaptureName(name))
            return Error("INVALID_REQUEST", "name must contain 1-100 letters, numbers, '.', '_', or '-'.");
        if (pageId is <= 0)
            return Error("INVALID_REQUEST", "pageId must be greater than zero when provided.");
        if (channelThreshold is < 0 or > 255)
            return Error("INVALID_REQUEST", "channelThreshold must be between 0 and 255.");

        var normalizedAction = action.Trim().ToLowerInvariant();
        if (normalizedAction is not ("baseline" or "compare"))
            return Error("INVALID_REQUEST", "action must be baseline or compare.");

        var prepare = await RunPowerShellAsync(
            deviceId,
            path,
            DeveloperWorkflowScripts.PrepareVisualDirectory(path, name),
            60,
            262_144);
        if (!prepare.Success || prepare.Data is null)
            return FromCommandFailure(prepare);

        var paths = ParseLastJson(prepare.Data.Stdout);
        if (paths is null || paths.Value.ValueKind != JsonValueKind.Object)
            return Error("INTERNAL_ERROR", "Visual regression directory preparation returned invalid data.");

        var baselinePath = paths.Value.GetProperty("baselinePath").GetString();
        var currentPath = paths.Value.GetProperty("currentPath").GetString();
        var diffPath = paths.Value.GetProperty("diffPath").GetString();
        if (baselinePath is null || currentPath is null || diffPath is null)
            return Error("INTERNAL_ERROR", "Visual regression paths are missing.");

        object? selection = null;
        if (pageId.HasValue)
        {
            selection = DescribeExternal(await CallChromeAsync(
                "select_page",
                new { pageId = pageId.Value, bringToFront = false }));
        }

        var capturePath = normalizedAction == "baseline" ? baselinePath : currentPath;
        var capture = await CallChromeAsync(
            "take_screenshot",
            new { format = "png", fullPage, filePath = capturePath });
        if (capture.IsError == true)
            return capture;

        if (normalizedAction == "baseline")
        {
            return Json(new
            {
                action = normalizedAction,
                selection,
                capture = DescribeExternal(capture),
                baselinePath,
                currentPath,
                diffPath
            });
        }

        var compare = await RunPowerShellAsync(
            deviceId,
            path,
            DeveloperWorkflowScripts.CompareVisuals(baselinePath, currentPath, diffPath, channelThreshold),
            180,
            1_048_576);
        if (!compare.Success || compare.Data is null)
            return FromCommandFailure(compare);

        return Json(new
        {
            action = normalizedAction,
            selection,
            capture = DescribeExternal(capture),
            comparisonExecution = compare.Data,
            comparison = ParseLastJson(compare.Data.Stdout)
        });
    }

    [McpServerTool(
        Name = "repo_task_checkpoint",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false),
     Description("Saves, reads, lists, or clears lightweight repository task checkpoints under .agentbridge/checkpoints.jsonl. Checkpoints contain Git HEAD/branch, changed-file metadata, a short note, and test summary, never full diffs. Duplicate saves are debounced and history is capped. Requires dev:execute scope.")]
    public async Task<CallToolResult> RepoTaskCheckpointAsync(
        [Description("Optional target device id. Omit to use the active desktop agent."), Optional, DefaultParameterValue(null)] string? deviceId,
        [Description("Absolute repository directory")] string path,
        [Description("Action: save, latest, list, or clear (default: save)")] string action = "save",
        [Description("Short checkpoint note used by action=save")] string? note = null,
        [Description("Short summary of tests/build checks used by action=save")] string? testSummary = null,
        [Description("Suppress an identical save made within this many seconds (default: 10, range: 0-300)")] int debounceSeconds = 10,
        [Description("Maximum checkpoints retained (default: 200, range: 10-1000)")] int maxEntries = 200,
        [Description("Number of checkpoints returned by action=list (default: 20, range: 1-200)")] int listCount = 20)
    {
        var authError = await RequireDevExecuteAsync();
        if (authError is not null)
            return authError;
        if (string.IsNullOrWhiteSpace(path))
            return Error("INVALID_REQUEST", "path is required.");
        if (note is { Length: > 2_000 } || testSummary is { Length: > 4_000 })
            return Error("INVALID_REQUEST", "note/testSummary is too long.");
        if (note?.Any(char.IsControl) == true && note.Any(character => character is not '\r' and not '\n' and not '\t'))
            return Error("INVALID_REQUEST", "note contains unsupported control characters.");
        if (testSummary?.Any(char.IsControl) == true && testSummary.Any(character => character is not '\r' and not '\n' and not '\t'))
            return Error("INVALID_REQUEST", "testSummary contains unsupported control characters.");
        if (debounceSeconds is < 0 or > 300)
            return Error("INVALID_REQUEST", "debounceSeconds must be between 0 and 300.");
        if (maxEntries is < 10 or > 1_000)
            return Error("INVALID_REQUEST", "maxEntries must be between 10 and 1000.");
        if (listCount is < 1 or > 200)
            return Error("INVALID_REQUEST", "listCount must be between 1 and 200.");

        var normalizedAction = action.Trim().ToLowerInvariant();
        if (normalizedAction is not ("save" or "latest" or "list" or "clear"))
            return Error("INVALID_REQUEST", "action must be save, latest, list, or clear.");

        var result = await RunPowerShellAsync(
            deviceId,
            path,
            DeveloperWorkflowScripts.RepoCheckpoint(
                path,
                normalizedAction,
                note,
                testSummary,
                debounceSeconds,
                maxEntries,
                listCount),
            90,
            2_097_152);
        if (!result.Success || result.Data is null)
            return FromCommandFailure(result);

        return Json(new
        {
            execution = result.Data,
            data = ParseLastJson(result.Data.Stdout)
        });
    }

    private async Task<CommandResult<PowerShellExecuteResult>> RunPowerShellAsync(
        string? deviceId,
        string workingDirectory,
        string script,
        int timeoutSeconds,
        int maxOutputBytes)
    {
        return await _dispatcher.SendAsync<PowerShellExecuteResult>(
            new PowerShellExecuteCommand
            {
                CommandId = Guid.NewGuid(),
                DeviceId = deviceId ?? string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
                WorkingDirectory = workingDirectory,
                Script = script,
                Visible = false,
                Elevated = false,
                TimeoutSeconds = timeoutSeconds,
                MaxOutputBytes = maxOutputBytes
            },
            RequestToken());
    }

    private async Task<CallToolResult> CallChromeAsync(string toolName, object arguments)
    {
        try
        {
            return await _externalRouter.CallToolAsync(
                new CallToolRequestParams
                {
                    Name = $"chrome-devtools.{toolName}",
                    Arguments = ToArguments(arguments)
                },
                RequestToken());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chrome DevTools workflow call failed for {ToolName}", toolName);
            return Error("BROWSER_TOOL_FAILED", $"Chrome DevTools call '{toolName}' failed: {ex.Message}");
        }
    }

    private async Task<CallToolResult?> RequireDevExecuteAsync()
    {
        var principal = _httpContextAccessor?.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        var authorized = await _authorizationService.AuthorizeAsync(principal, null, "DevExecutePolicy");
        return authorized.Succeeded
            ? null
            : Error("FORBIDDEN", "Access denied. Required scope: dev:execute");
    }

    private CancellationToken RequestToken() =>
        _httpContextAccessor?.HttpContext?.RequestAborted ?? CancellationToken.None;

    private static Dictionary<string, JsonElement> ToArguments(object value)
    {
        var element = JsonSerializer.SerializeToElement(value, JsonOptions.Default);
        return element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
    }

    private static object DescribeExternal(CallToolResult result)
    {
        var text = result.Content
            .OfType<TextContentBlock>()
            .Select(block => block.Text)
            .ToArray();
        return new
        {
            success = result.IsError != true,
            text,
            nonTextContentCount = result.Content.Count - text.Length
        };
    }

    private static JsonElement? ParseLastJson(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!(line.StartsWith('{') || line.StartsWith('[')))
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Ignore non-JSON output and continue looking upward.
            }
        }

        return null;
    }

    private static string BuildDomTraceStartFunction(int maxSamples)
    {
        return $$"""
() => {
  globalThis.__agentBridgeDomTrace?.stop?.();
  const maxSamples = {{maxSamples}};
  const startedAt = performance.now();
  const samples = { mutations: [], listeners: [], timers: [], intervals: [], animationFrames: [] };
  const counts = { mutations: 0, addedNodes: 0, removedNodes: 0, attributes: 0, characterData: 0, listeners: 0, timers: 0, intervals: 0, animationFrames: 0 };
  const duplicateKeys = new Map();
  const original = {
    addEventListener: EventTarget.prototype.addEventListener,
    removeEventListener: EventTarget.prototype.removeEventListener,
    setTimeout: globalThis.setTimeout,
    clearTimeout: globalThis.clearTimeout,
    setInterval: globalThis.setInterval,
    clearInterval: globalThis.clearInterval,
    requestAnimationFrame: globalThis.requestAnimationFrame,
    cancelAnimationFrame: globalThis.cancelAnimationFrame
  };
  const label = target => {
    if (target === globalThis) return 'window';
    if (target === document) return 'document';
    if (target?.nodeType === 1) {
      const id = target.id ? `#${target.id}` : '';
      const classes = typeof target.className === 'string' && target.className ? `.${target.className.trim().split(/\s+/).slice(0, 3).join('.')}` : '';
      return `${target.tagName?.toLowerCase?.() ?? 'element'}${id}${classes}`;
    }
    return target?.constructor?.name ?? typeof target;
  };
  const push = (bucket, value) => { if (bucket.length < maxSamples) bucket.push(value); };
  EventTarget.prototype.addEventListener = function(type, listener, options) {
    counts.listeners++;
    const key = `${label(this)}|${String(type)}|${listener?.name ?? 'anonymous'}|${JSON.stringify(options ?? null)}`;
    duplicateKeys.set(key, (duplicateKeys.get(key) ?? 0) + 1);
    push(samples.listeners, { atMs: performance.now() - startedAt, target: label(this), type: String(type), listener: listener?.name ?? 'anonymous', countForKey: duplicateKeys.get(key) });
    return original.addEventListener.call(this, type, listener, options);
  };
  EventTarget.prototype.removeEventListener = function(type, listener, options) {
    return original.removeEventListener.call(this, type, listener, options);
  };
  globalThis.setTimeout = function(callback, delay, ...args) {
    counts.timers++;
    push(samples.timers, { atMs: performance.now() - startedAt, delay: Number(delay ?? 0), callback: callback?.name ?? 'anonymous' });
    return original.setTimeout.call(this, callback, delay, ...args);
  };
  globalThis.clearTimeout = function(id) { return original.clearTimeout.call(this, id); };
  globalThis.setInterval = function(callback, delay, ...args) {
    counts.intervals++;
    push(samples.intervals, { atMs: performance.now() - startedAt, delay: Number(delay ?? 0), callback: callback?.name ?? 'anonymous' });
    return original.setInterval.call(this, callback, delay, ...args);
  };
  globalThis.clearInterval = function(id) { return original.clearInterval.call(this, id); };
  if (typeof original.requestAnimationFrame === 'function') {
    globalThis.requestAnimationFrame = function(callback) {
      counts.animationFrames++;
      push(samples.animationFrames, { atMs: performance.now() - startedAt, callback: callback?.name ?? 'anonymous' });
      return original.requestAnimationFrame.call(this, callback);
    };
    globalThis.cancelAnimationFrame = function(id) { return original.cancelAnimationFrame.call(this, id); };
  }
  const observer = new MutationObserver(records => {
    for (const record of records) {
      counts.mutations++;
      counts.addedNodes += record.addedNodes?.length ?? 0;
      counts.removedNodes += record.removedNodes?.length ?? 0;
      if (record.type === 'attributes') counts.attributes++;
      if (record.type === 'characterData') counts.characterData++;
      push(samples.mutations, {
        atMs: performance.now() - startedAt,
        type: record.type,
        target: label(record.target),
        attributeName: record.attributeName ?? null,
        addedNodes: record.addedNodes?.length ?? 0,
        removedNodes: record.removedNodes?.length ?? 0
      });
    }
  });
  observer.observe(document, { subtree: true, childList: true, attributes: true, characterData: true });
  const snapshot = active => ({
    active,
    url: location.href,
    durationMs: performance.now() - startedAt,
    counts: { ...counts },
    duplicateListeners: [...duplicateKeys.entries()].filter(([, count]) => count > 1).sort((a, b) => b[1] - a[1]).slice(0, maxSamples).map(([key, count]) => ({ key, count })),
    samples
  });
  const stop = () => {
    observer.disconnect();
    EventTarget.prototype.addEventListener = original.addEventListener;
    EventTarget.prototype.removeEventListener = original.removeEventListener;
    globalThis.setTimeout = original.setTimeout;
    globalThis.clearTimeout = original.clearTimeout;
    globalThis.setInterval = original.setInterval;
    globalThis.clearInterval = original.clearInterval;
    if (original.requestAnimationFrame) globalThis.requestAnimationFrame = original.requestAnimationFrame;
    if (original.cancelAnimationFrame) globalThis.cancelAnimationFrame = original.cancelAnimationFrame;
    const result = snapshot(false);
    delete globalThis.__agentBridgeDomTrace;
    return result;
  };
  globalThis.__agentBridgeDomTrace = { stop, snapshot: () => snapshot(true) };
  return snapshot(true);
}
""";
    }

    private static bool IsSafeWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool ValidSimpleName(string value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maxLength
        && SimpleNameRegex().IsMatch(value);

    private static bool ValidCaptureName(string value) =>
        value.Length is >= 1 and <= 100 && CaptureNameRegex().IsMatch(value);

    private static bool ValidRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Any(char.IsControl))
            return false;
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(segment => segment is not "." and not "..");
    }

    private static CallToolResult Json(object value) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, JsonOptions.Default) }],
        IsError = false
    };

    private static CallToolResult FromCommandFailure<T>(CommandResult<T> result) =>
        Error(
            result.Error?.Code ?? "INTERNAL_ERROR",
            result.Error?.Message ?? "Command execution failed.");

    private static CallToolResult Error(string code, string message) => new()
    {
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }],
        IsError = true
    };

    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleNameRegex();

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CaptureNameRegex();
}
