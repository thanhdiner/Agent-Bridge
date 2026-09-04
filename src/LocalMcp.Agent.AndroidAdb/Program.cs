using LocalMcp.Agent.AndroidAdb;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddEnvironmentVariables();
builder.Services.AddAndroidAdbAgent(builder.Configuration);
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
