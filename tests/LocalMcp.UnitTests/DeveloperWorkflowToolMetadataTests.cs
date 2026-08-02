using System.ComponentModel;
using System.Reflection;
using LocalMcp.Gateway.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class DeveloperWorkflowToolMetadataTests
{
    public static TheoryData<string, bool, bool, bool, bool, string[]> ToolCases => new()
    {
        {
            "extension_dev_workflow", false, true, false, true,
            ["deviceId", "path", "packageScript", "reloadSelectedExtension", "testUrl", "collectConsole", "collectNetwork", "timeoutSeconds"]
        },
        {
            "browser_extension_inspect", true, false, true, true,
            ["pageId", "serviceWorkerId", "maxConsoleMessages", "maxNetworkRequests", "includeStorage"]
        },
        {
            "dom_event_trace", false, false, false, true,
            ["pageId", "durationMs", "maxSamples", "collectConsole"]
        },
        {
            "process_tree_supervisor", false, true, false, false,
            ["deviceId", "action", "path", "rootPath", "nameContains", "includePorts", "maxResults", "processId", "expectedProcessName", "entireProcessTree", "timeoutMs"]
        },
        {
            "dev_session_run", false, true, false, true,
            ["deviceId", "action", "path", "configRelativePath", "profileName", "sessionId", "stdoutOffset", "stderrOffset", "maxOutputBytes", "timeoutSeconds"]
        },
        {
            "visual_regression_compare", false, false, false, true,
            ["deviceId", "path", "action", "name", "pageId", "fullPage", "channelThreshold"]
        },
        {
            "repo_task_checkpoint", false, true, false, false,
            ["deviceId", "path", "action", "note", "testSummary", "debounceSeconds", "maxEntries", "listCount"]
        }
    };

    [Theory]
    [MemberData(nameof(ToolCases))]
    public void Tools_HaveExpectedMetadataAndSchema(
        string toolName,
        bool readOnly,
        bool destructive,
        bool idempotent,
        bool openWorld,
        string[] parameters)
    {
        var method = FindTool(toolName);
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
        var description = method.GetCustomAttribute<DescriptionAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(readOnly, attribute!.ReadOnly);
        Assert.Equal(destructive, attribute.Destructive);
        Assert.Equal(idempotent, attribute.Idempotent);
        Assert.Equal(openWorld, attribute.OpenWorld);
        Assert.False(string.IsNullOrWhiteSpace(description?.Description));
        Assert.Equal(parameters, method.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void ToolType_ExportsExactlySevenWorkflowTools()
    {
        var names = typeof(DeveloperWorkflowTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "browser_extension_inspect",
                "dev_session_run",
                "dom_event_trace",
                "extension_dev_workflow",
                "process_tree_supervisor",
                "repo_task_checkpoint",
                "visual_regression_compare"
            },
            names);
    }

    [Fact]
    public void ToolVisibility_GroupsWorkflowToolsAndMarksMutatingToolsDangerous()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentBridgeTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var store = new ToolVisibilityStore(
                NullLogger<ToolVisibilityStore>.Instance,
                Path.Combine(directory, "tool-visibility.json"));
            store.RememberCatalog(
                ToolCases.Select(toolCase =>
                {
                    var name = Assert.IsType<string>(toolCase[0]);
                    return new Tool { Name = name, Title = name };
                }),
                []);

            var tools = store.GetSnapshot().Groups.SelectMany(group => group.Tools).ToArray();

            Assert.All(tools, tool => Assert.Equal("Developer Workflows", tool.Group));
            Assert.Equal("safe", Assert.Single(tools, tool => tool.Name == "browser_extension_inspect").Risk);
            Assert.Equal("dangerous", Assert.Single(tools, tool => tool.Name == "extension_dev_workflow").Risk);
            Assert.Equal("dangerous", Assert.Single(tools, tool => tool.Name == "dev_session_run").Risk);
            Assert.Equal("dangerous", Assert.Single(tools, tool => tool.Name == "repo_task_checkpoint").Risk);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Tools_DoNotExposeInternalParameters()
    {
        var forbidden = new[]
        {
            "CancellationToken",
            "HttpContext",
            "ClaimsPrincipal",
            "IServiceProvider",
            "Object"
        };

        var parameters = typeof(DeveloperWorkflowTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(method => method.GetParameters());

        foreach (var parameter in parameters)
            Assert.DoesNotContain(parameter.ParameterType.Name, forbidden);
    }

    private static MethodInfo FindTool(string toolName) =>
        typeof(DeveloperWorkflowTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
}
