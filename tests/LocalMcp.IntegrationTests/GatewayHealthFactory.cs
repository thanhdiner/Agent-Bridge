using LocalMcp.Gateway.Hubs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LocalMcp.IntegrationTests;

public sealed class GatewayHealthFactory : WebApplicationFactory<AgentHub>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AuthenticationEnabled"] = "false",
                ["Security:PublicExposure"] = "false",
                ["AgentSecurity:AuthenticationEnabled"] = "false"
            });
        });
    }
}
