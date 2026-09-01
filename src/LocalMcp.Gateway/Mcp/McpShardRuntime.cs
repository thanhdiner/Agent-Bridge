using ModelContextProtocol.Protocol;

namespace LocalMcp.Gateway.Mcp;

public static class McpShardRuntime
{
    private const string AndroidToolPrefix = "android_";

    public static IReadOnlyList<Tool> ExportToolsForConnection(
        IEnumerable<Tool> localTools,
        IEnumerable<Tool> externalTools,
        ToolVisibilityStore visibilityStore,
        string connection)
    {
        var androidConnection = IsAndroidConnection(connection);
        var filteredLocalTools = localTools
            .Where(tool => IsAndroidTool(tool.Name) == androidConnection)
            .Where(tool => androidConnection || visibilityStore.IsToolEnabledForConnection(tool.Name, connection))
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var filteredExternalTools = (androidConnection ? Array.Empty<Tool>() : externalTools)
            .Where(tool => visibilityStore.IsToolEnabledForConnection(tool.Name, connection))
            .OrderBy(tool => ResolveExternalNamespace(tool.Name), StringComparer.OrdinalIgnoreCase)
            .ThenBy(tool => ResolveExternalToolName(tool.Name), StringComparer.OrdinalIgnoreCase);

        return filteredLocalTools.Concat(filteredExternalTools).ToArray();
    }

    public static async Task<CallToolResult> CallToolAsync(
        CallToolRequestParams request,
        string connection,
        ToolVisibilityStore visibilityStore,
        IExternalMcpRouter externalRouter,
        Func<CallToolRequestParams, CancellationToken, Task<CallToolResult>> invokeLocalTool,
        CancellationToken cancellationToken)
    {
        var requestedName = request.Name;
        if (string.IsNullOrWhiteSpace(requestedName))
            return Error("INVALID_REQUEST", "Tool name is required.");

        var androidConnection = IsAndroidConnection(connection);
        if (IsAndroidTool(requestedName) != androidConnection)
        {
            return Error(
                "TOOL_NOT_AVAILABLE_ON_ENDPOINT",
                androidConnection
                    ? $"Tool '{requestedName}' is not available on the Android MCP endpoint."
                    : $"Tool '{requestedName}' is available only on the Android MCP endpoint.");
        }

        if (!androidConnection && !visibilityStore.IsToolEnabledForConnection(requestedName, connection))
            return Error("TOOL_DISABLED", $"Tool '{requestedName}' is not enabled for AgentBridge connection {connection}.");

        if (externalRouter.IsExternalToolName(requestedName))
            return await externalRouter.CallToolAsync(request, cancellationToken);

        return await invokeLocalTool(request, cancellationToken);
    }

    public static string ResolveExternalNamespace(string? toolName)
    {
        var name = toolName?.Trim() ?? string.Empty;
        var dotIndex = name.IndexOf('.', StringComparison.Ordinal);
        return dotIndex <= 0 ? string.Empty : name[..dotIndex];
    }

    public static string ResolveExternalToolName(string? toolName)
    {
        var name = toolName?.Trim() ?? string.Empty;
        var dotIndex = name.IndexOf('.', StringComparison.Ordinal);
        return dotIndex < 0 || dotIndex == name.Length - 1 ? name : name[(dotIndex + 1)..];
    }

    private static bool IsAndroidConnection(string? connection) =>
        string.Equals(connection, ToolVisibilityStore.ConnectionAndroidA, StringComparison.Ordinal);

    private static bool IsAndroidTool(string? toolName) =>
        toolName?.StartsWith(AndroidToolPrefix, StringComparison.OrdinalIgnoreCase) == true;

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }]
    };
}
