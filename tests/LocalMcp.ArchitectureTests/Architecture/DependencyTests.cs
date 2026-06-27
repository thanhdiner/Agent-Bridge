using System.Reflection;
using LocalMcp.Contracts.Commands;
using LocalMcp.BuildingBlocks.Errors;
using Xunit;

namespace LocalMcp.ArchitectureTests.Architecture;

public sealed class DependencyTests
{
    [Fact]
    public void ContractsAssembly_ShouldNotReferenceForbiddenAssemblies()
    {
        var contractsAssembly = typeof(AgentCommand).Assembly;
        var references = contractsAssembly.GetReferencedAssemblies().Select(r => r.Name).ToList();

        Assert.DoesNotContain("LocalMcp.Gateway", references);
        Assert.DoesNotContain("LocalMcp.Agent.Windows", references);
        Assert.DoesNotContain("Microsoft.AspNetCore.App", references);
        Assert.DoesNotContain("Microsoft.AspNetCore.Http", references);
        Assert.DoesNotContain("Microsoft.AspNetCore.Mvc", references);
        Assert.DoesNotContain("Microsoft.AspNetCore.SignalR", references);
    }

    [Fact]
    public void BuildingBlocksAssembly_ShouldNotReferenceForbiddenAssemblies()
    {
        var buildingBlocksAssembly = typeof(ErrorCodes).Assembly;
        var references = buildingBlocksAssembly.GetReferencedAssemblies().Select(r => r.Name).ToList();

        Assert.DoesNotContain("LocalMcp.Gateway", references);
        Assert.DoesNotContain("LocalMcp.Agent.Windows", references);
    }
}
