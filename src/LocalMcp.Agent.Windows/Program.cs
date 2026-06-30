using LocalMcp.Agent.Windows;
using LocalMcp.BuildingBlocks.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

var localConfigurationPath = LocalConfigurationPaths.GetConfigurationFilePath();
builder.Configuration.AddJsonFile(
    localConfigurationPath,
    optional: true,
    reloadOnChange: false);

// Environment variables remain the highest-priority machine override.
builder.Configuration.AddEnvironmentVariables();

// Add services
builder.Services.AddAgentServices(builder.Configuration);

// Add Hosted worker
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
