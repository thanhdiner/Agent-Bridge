using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Contracts.Commands;
using LocalMcp.Gateway.Mcp;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class PowerShellExecuteTests
{
    private static MethodInfo GetToolMethod(string toolName = "powershell_exec")
    {
        var method = typeof(FileSystemTools)
            .GetMethods()
            .SingleOrDefault(candidate =>
                candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name ==
                toolName);

        Assert.NotNull(method);
        return method!;
    }

    [Fact]
    public void CommandDefaults_AreBounded()
    {
        var command = new PowerShellExecuteCommand
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkingDirectory = "C:/repo",
            Script = "git status"
        };

        Assert.False(command.Visible);
        Assert.False(command.Elevated);
        Assert.Equal(120, command.TimeoutSeconds);
        Assert.Equal(1_048_576, command.MaxOutputBytes);
        Assert.Equal(
            TimeSpan.FromSeconds(135),
            AgentCommandTimeouts.GetTimeout(command));
    }

    [Fact]
    public void ToolMetadata_RequiresConfirmationAndExposesExactSchema()
    {
        var method = GetToolMethod();
        var tool = method.GetCustomAttribute<McpServerToolAttribute>();
        var description = method.GetCustomAttribute<DescriptionAttribute>();

        Assert.NotNull(tool);
        Assert.False(tool!.ReadOnly);
        Assert.True(tool.Destructive);
        Assert.False(tool.Idempotent);
        Assert.True(tool.OpenWorld);

        Assert.NotNull(description);
        Assert.Contains(
            "Ask the user for confirmation",
            description!.Description,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "not filesystem-sandboxed",
            description.Description,
            StringComparison.OrdinalIgnoreCase);

        var names = method.GetParameters()
            .Select(parameter => parameter.Name!)
            .ToHashSet();

        Assert.True(new[]
        {
            "deviceId",
            "workingDirectory",
            "script",
            "timeoutSeconds",
            "maxOutputBytes"
        }.ToHashSet().SetEquals(names));
    }

    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("CLIENT_SECRET")]
    [InlineData("DATABASE_PASSWORD")]
    [InlineData("MY_API_KEY")]
    [InlineData("SESSION_COOKIE")]
    [InlineData("AZURE_CREDENTIAL")]
    public void IsSensitiveEnvironmentVariable_RejectsSecretLikeNames(string name)
    {
        Assert.True(FileSystemExecutor.IsSensitiveEnvironmentVariable(name));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("USERPROFILE")]
    [InlineData("LOCALAPPDATA")]
    [InlineData("DOTNET_ROOT")]
    public void IsSensitiveEnvironmentVariable_AllowsRequiredRuntimeNames(string name)
    {
        Assert.False(FileSystemExecutor.IsSensitiveEnvironmentVariable(name));
    }

    [Fact]
    public void BoundPowerShellOutput_WhenWithinBudget_PreservesBothStreams()
    {
        var result = FileSystemExecutor.BoundPowerShellOutput(
            "hello",
            "warning",
            1024);

        Assert.Equal("hello", result.Stdout);
        Assert.Equal("warning", result.Stderr);
        Assert.False(result.Truncated);
        Assert.Equal(
            Encoding.UTF8.GetByteCount("hellowarning"),
            result.BytesReturned);
    }

    [Fact]
    public void BoundPowerShellOutput_WhenOverBudget_DoesNotSplitUtf8Runes()
    {
        var result = FileSystemExecutor.BoundPowerShellOutput(
            new string('a', 100) + "🧪",
            new string('b', 100) + "🧰",
            64);

        Assert.True(result.Truncated);
        Assert.InRange(result.BytesReturned, 1, 64);
        Assert.True(
            Encoding.UTF8.GetByteCount(result.Stdout) +
            Encoding.UTF8.GetByteCount(result.Stderr) <= 64);
    }
}
