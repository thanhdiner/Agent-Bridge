using ModelContextProtocol.Protocol;

namespace LocalMcp.Gateway.Mcp;

internal static class ToolRuntimeHelpers
{
    public static void SuppressSdkLocalToolAppend(object toolCollection, ILogger logger)
    {
        var clearMethod = toolCollection.GetType().GetMethod("Clear", Type.EmptyTypes);
        if (clearMethod is null)
        {
            logger.LogWarning(
                "Could not suppress SDK local tool append because ToolCollection type {ToolCollectionType} has no public Clear method. Local tools may still appear in tools/list.",
                toolCollection.GetType().FullName);
            return;
        }

        try
        {
            clearMethod.Invoke(toolCollection, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not suppress SDK local tool append for ToolCollection type {ToolCollectionType}. Local tools may still appear in tools/list.",
                toolCollection.GetType().FullName);
        }
    }

    public static async Task<CallToolResult> InvokeLocalPrimitiveAsync(
        object primitive,
        object callContext,
        CancellationToken cancellationToken)
    {
        callContext.GetType().GetProperty("MatchedPrimitive")?.SetValue(callContext, primitive);

        var invokeMethod = primitive.GetType().GetMethods()
            .FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, "InvokeAsync", StringComparison.Ordinal))
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 2 &&
                       parameters[0].ParameterType.IsAssignableFrom(callContext.GetType()) &&
                       parameters[1].ParameterType == typeof(CancellationToken);
            });

        if (invokeMethod is null)
        {
            return Error("LOCAL_TOOL_INVOKE_FAILED", "Local MCP tool cannot be invoked by the current AgentBridge runtime.");
        }

        var invokeResult = invokeMethod.Invoke(primitive, new object[] { callContext, cancellationToken });
        if (invokeResult is Task<CallToolResult> typedTask)
        {
            return await typedTask;
        }

        if (invokeResult is Task task)
        {
            await task;
            var result = task.GetType().GetProperty("Result")?.GetValue(task);
            if (result is CallToolResult callToolResult)
                return callToolResult;
        }

        return Error("LOCAL_TOOL_INVOKE_FAILED", "Local MCP tool returned an unsupported result.");
    }

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"Error [{code}]: {message}" }]
    };
}
