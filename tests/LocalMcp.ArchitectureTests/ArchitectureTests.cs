using System.Reflection;

namespace LocalMcp.ArchitectureTests;

public sealed class DependencyTests
{
    [Fact]
    public void Contracts_ShouldNotReferenceGatewayOrAgent()
    {
        var contractsAssembly = Assembly.Load("LocalMcp.Contracts");
        var references = contractsAssembly.GetReferencedAssemblies();

        foreach (var reference in references)
        {
            Assert.DoesNotContain("LocalMcp.Gateway", reference.Name);
            Assert.DoesNotContain("LocalMcp.Agent.Windows", reference.Name);
        }
    }

    [Fact]
    public void BuildingBlocks_ShouldNotReferenceGatewayOrAgent()
    {
        var bbAssembly = Assembly.Load("LocalMcp.BuildingBlocks");
        var references = bbAssembly.GetReferencedAssemblies();

        foreach (var reference in references)
        {
            Assert.DoesNotContain("LocalMcp.Gateway", reference.Name);
            Assert.DoesNotContain("LocalMcp.Agent.Windows", reference.Name);
        }
    }
}
