using LocalMcp.Agent.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Add services
builder.Services.AddAgentServices(builder.Configuration);

// Add Hosted worker
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
