using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using LocalMcp.Gateway.Hubs;
using LocalMcp.Gateway.Connections;
using LocalMcp.Gateway.Commands;
using LocalMcp.Gateway.Mcp;
using LocalMcp.Agent.Windows.Connection;
using LocalMcp.Agent.Windows.Security;
using LocalMcp.Agent.Windows.FileSystem;
using LocalMcp.Agent.Windows.Commands;
using LocalMcp.Contracts.Results;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using Xunit.Abstractions;

namespace LocalMcp.IntegrationTests;

[Collection("Sequential")]
public sealed class BenchmarkTests : IAsyncDisposable
{
    private readonly ITestOutputHelper _output;
    private WebApplication? _gatewayApp;
    private GatewayConnection? _agentConnection;
    private string? _tempRoot;
    private string? _gatewayUrl;
    private readonly string _deviceId = "benchmark-device-" + Guid.NewGuid();

    public BenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Loopback, 0);
        });

        builder.Services.AddGatewayServices();
        builder.Services.AddSignalR();
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<FileSystemTools>();

        _gatewayApp = builder.Build();
        _gatewayApp.MapHub<AgentHub>("/hubs/agent");
        _gatewayApp.MapMcp();

        await _gatewayApp.StartAsync();

        var boundAddress = _gatewayApp.Urls.First();
        _gatewayUrl = boundAddress;

        _tempRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_Bench_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);

        var agentOptions = new AgentOptions
        {
            DeviceId = _deviceId,
            GatewayUrl = _gatewayUrl
        };

        var fileAccessOptions = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            MaxReadBytes = 1024 * 1024
        };

        var pathPolicy = new PathPolicy(Options.Create(fileAccessOptions));
        var executor = new FileSystemExecutor(pathPolicy, NullLogger<FileSystemExecutor>.Instance);
        var handler = new CommandHandler(pathPolicy, executor, NullLogger<CommandHandler>.Instance);

        _agentConnection = new GatewayConnection(
            Options.Create(agentOptions),
            Options.Create(new AgentSecurityOptions()),
            handler,
            NullLogger<GatewayConnection>.Instance
        );

        await _agentConnection.StartAsync(CancellationToken.None);

        // Wait 500ms to ensure fully negotiated
        await Task.Delay(500);
    }

    [Fact]
    public async Task Run_100_FsRead_Calls_And_Measure()
    {
        await InitializeAsync();

        Assert.NotNull(_tempRoot);
        Assert.NotNull(_gatewayApp);

        var fileName = "bench.txt";
        var filePath = Path.Combine(_tempRoot, fileName);
        var text = "Bench file content. Lightweight and fast.";
        await File.WriteAllTextAsync(filePath, text);

        var mcpTools = new FileSystemTools(
            _gatewayApp.Services.GetRequiredService<ICommandDispatcher>(),
            NullLogger<FileSystemTools>.Instance
        );

        // Warm up (10 calls)
        for (int i = 0; i < 10; i++)
        {
            await mcpTools.ReadFileAsync(_deviceId, filePath, CancellationToken.None);
        }

        var timings = new List<double>();
        var sw = new Stopwatch();

        // 100 benchmark calls
        for (int i = 0; i < 100; i++)
        {
            sw.Restart();
            var response = await mcpTools.ReadFileAsync(_deviceId, filePath, CancellationToken.None);
            sw.Stop();

            Assert.False(response.IsError);
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        // Sort timings to calculate percentiles
        timings.Sort();

        double min = timings[0];
        double max = timings[timings.Count - 1];
        double sum = 0;
        foreach (var t in timings) sum += t;
        double average = sum / timings.Count;

        double median = timings[timings.Count / 2];
        double p95 = timings[(int)(timings.Count * 0.95)];

        _output.WriteLine($"--- BENCHMARK RESULTS FOR 100 fs_read CALLS ---");
        _output.WriteLine($"Minimum Duration : {min:F2} ms");
        _output.WriteLine($"Average Duration : {average:F2} ms");
        _output.WriteLine($"Median Duration  : {median:F2} ms");
        _output.WriteLine($"95th Percentile  : {p95:F2} ms");
        _output.WriteLine($"Maximum Duration : {max:F2} ms");
        _output.WriteLine($"----------------------------------------------");
    }

    public async ValueTask DisposeAsync()
    {
        if (_agentConnection is not null)
        {
            await _agentConnection.StopAsync(CancellationToken.None);
            await _agentConnection.DisposeAsync();
        }

        if (_gatewayApp is not null)
        {
            await _gatewayApp.StopAsync();
            await _gatewayApp.DisposeAsync();
        }

        if (_tempRoot is not null && Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, true);
            }
            catch { }
        }
    }
}
