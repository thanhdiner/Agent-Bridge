using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
using ModelContextProtocol.Protocol;

namespace LocalMcp.IntegrationTests;

[Collection("Sequential")]
public sealed class EndToEndTests : IAsyncDisposable
{
    private WebApplication? _gatewayApp;
    private GatewayConnection? _agentConnection;
    private string? _tempRoot;
    private string? _gatewayUrl;
    private readonly string _deviceId = "e2e-test-device-" + Guid.NewGuid();

    private async Task InitializeAsync()
    {
        // 1. Start Gateway on ephemeral port
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // Keep logs clean during tests
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Loopback, 0); // Bind to dynamic ephemeral port
        });

        // Register Gateway DI
        builder.Services.AddGatewayServices();
        builder.Services.AddSignalR();
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<FileSystemTools>();

        _gatewayApp = builder.Build();
        _gatewayApp.MapHub<AgentHub>("/hubs/agent");
        _gatewayApp.MapMcp();

        await _gatewayApp.StartAsync();

        // Retrieve dynamic gateway URL
        var boundAddress = _gatewayApp.Urls.First();
        _gatewayUrl = boundAddress;

        // 2. Set up Agent Allowed Root
        _tempRoot = Path.Combine(Path.GetTempPath(), "LocalMcp_E2E_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);

        // 3. Start Agent GatewayConnection
        var agentOptions = new AgentOptions
        {
            DeviceId = _deviceId,
            GatewayUrl = _gatewayUrl
        };

        var fileAccessOptions = new FileAccessOptions
        {
            AllowedRoots = new List<string> { _tempRoot },
            WritableRoots = new List<string> { _tempRoot },
            DeniedSegments = new List<string> { ".git" },
            DeniedFileNames = new List<string> { ".env" },
            MaxReadBytes = 1024 * 1024
        };

        var pathPolicy = new PathPolicy(Options.Create(fileAccessOptions));
        var executor = new FileSystemExecutor(pathPolicy, Options.Create(fileAccessOptions), NullLogger<FileSystemExecutor>.Instance);
        var directoryCopyExecutor = new DirectoryCopyExecutor(
            executor,
            pathPolicy,
            Options.Create(fileAccessOptions),
            NullLogger<DirectoryCopyExecutor>.Instance);
        var handler = new CommandHandler(
            pathPolicy,
            executor,
            directoryCopyExecutor,
            NullLogger<CommandHandler>.Instance);

        _agentConnection = new GatewayConnection(
            Options.Create(agentOptions),
            Options.Create(new AgentSecurityOptions()),
            handler,
            NullLogger<GatewayConnection>.Instance
        );

        await _agentConnection.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FsRead_EndToEndFlow_ReadsFileSuccessfully()
    {
        await InitializeAsync();

        Assert.NotNull(_tempRoot);
        Assert.NotNull(_gatewayApp);

        // Create a temporary file to read
        var fileName = "hello.txt";
        var filePath = Path.Combine(_tempRoot, fileName);
        var text = "Hello from the E2E Integration test!";
        await File.WriteAllTextAsync(filePath, text);

        // Resolve dependencies from Gateway app to call the MCP tool
        var mcpTools = new FileSystemTools(
            _gatewayApp.Services.GetRequiredService<ICommandDispatcher>(),
            _gatewayApp.Services.GetRequiredService<IAuthorizationService>(),
            NullLogger<FileSystemTools>.Instance,
            _gatewayApp.Services.GetService<IHttpContextAccessor>()
        );

        // Call the tool
        var response = await mcpTools.ReadFileAsync(_deviceId, filePath);

        // Assert
        Assert.False(response.IsError);
        Assert.NotNull(response.Content);
        Assert.Single(response.Content);

        var textBlock = Assert.IsType<TextContentBlock>(response.Content[0]);
        var data = JsonSerializer.Deserialize<ReadFileResult>(textBlock.Text, JsonOptions.Default);

        Assert.NotNull(data);
        Assert.Equal(filePath, data.Path);
        Assert.Equal(text, data.Content);
        Assert.Equal("utf-8", data.Encoding);
        Assert.Equal(text.Length, data.Size);
        Assert.NotEmpty(data.Sha256);
    }

    [Fact]
    public async Task FsReadRange_EndToEndFlow_ReadsRequestedLines()
    {
        await InitializeAsync();

        Assert.NotNull(_tempRoot);
        Assert.NotNull(_gatewayApp);

        var filePath = Path.Combine(_tempRoot, "range.txt");
        var content = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"line-{i}"));
        await File.WriteAllTextAsync(filePath, content, new System.Text.UTF8Encoding(false));

        var mcpTools = new FileSystemTools(
            _gatewayApp.Services.GetRequiredService<ICommandDispatcher>(),
            _gatewayApp.Services.GetRequiredService<IAuthorizationService>(),
            NullLogger<FileSystemTools>.Instance,
            _gatewayApp.Services.GetService<IHttpContextAccessor>()
        );

        var response = await mcpTools.ReadRangeAsync(_deviceId, filePath, startLine: 4, lineCount: 3);

        Assert.False(response.IsError);
        Assert.Single(response.Content);

        var textBlock = Assert.IsType<TextContentBlock>(response.Content[0]);
        var data = JsonSerializer.Deserialize<ReadRangeResult>(textBlock.Text, JsonOptions.Default);

        Assert.NotNull(data);
        Assert.Equal(4L, data.StartLine);
        Assert.Equal(6L, data.EndLine);
        Assert.Equal(12L, data.TotalLines);
        Assert.Equal("line-4\nline-5\nline-6", data.Content);
        Assert.True(data.Truncated);
        Assert.NotEmpty(data.Sha256);
    }

    [Fact]
    public async Task FsRead_EndToEndFlow_ReportsPathOutsideAllowedRoot()
    {
        await InitializeAsync();

        Assert.NotNull(_gatewayApp);

        // Try reading a file that lies outside allowed root
        var outsidePath = Path.Combine(Path.GetTempPath(), "secret_outside.txt");
        await File.WriteAllTextAsync(outsidePath, "secret");

        try
        {
            var mcpTools = new FileSystemTools(
                _gatewayApp.Services.GetRequiredService<ICommandDispatcher>(),
                _gatewayApp.Services.GetRequiredService<IAuthorizationService>(),
                NullLogger<FileSystemTools>.Instance,
                _gatewayApp.Services.GetService<IHttpContextAccessor>()
            );

            // Call the tool
            var response = await mcpTools.ReadFileAsync(_deviceId, outsidePath);

            // Assert
            Assert.True(response.IsError);
            Assert.NotNull(response.Content);
            Assert.Single(response.Content);

            var textBlock = Assert.IsType<TextContentBlock>(response.Content[0]);
            Assert.Contains(ErrorCodes.PathOutsideAllowedRoot, textBlock.Text);
        }
        finally
        {
            if (File.Exists(outsidePath))
            {
                File.Delete(outsidePath);
            }
        }
    }

    [Fact]
    public async Task FsMkdirAndFsStat_EndToEndFlow_CreatesAndStatsSuccessfully()
    {
        await InitializeAsync();

        Assert.NotNull(_tempRoot);
        Assert.NotNull(_gatewayApp);

        var targetPath = Path.Combine(_tempRoot, "e2e_parent", "e2e_child");

        var mcpTools = new FileSystemTools(
            _gatewayApp.Services.GetRequiredService<ICommandDispatcher>(),
            _gatewayApp.Services.GetRequiredService<IAuthorizationService>(),
            NullLogger<FileSystemTools>.Instance,
            _gatewayApp.Services.GetService<IHttpContextAccessor>()
        );

        // 1. Stat non-existent folder
        var statResponseBefore = await mcpTools.StatAsync(_deviceId, targetPath);
        Assert.False(statResponseBefore.IsError);
        var textBlockBefore = Assert.IsType<TextContentBlock>(statResponseBefore.Content[0]);
        var statResultBefore = JsonSerializer.Deserialize<StatResult>(textBlockBefore.Text, JsonOptions.Default);
        Assert.NotNull(statResultBefore);
        Assert.False(statResultBefore.Exists);

        // 2. Create folder recursively
        var mkdirResponse = await mcpTools.CreateDirectoryAsync(_deviceId, targetPath, recursive: true);
        Assert.False(mkdirResponse.IsError);
        var textBlockMkdir = Assert.IsType<TextContentBlock>(mkdirResponse.Content[0]);
        var mkdirResult = JsonSerializer.Deserialize<CreateDirectoryResult>(textBlockMkdir.Text, JsonOptions.Default);
        Assert.NotNull(mkdirResult);
        Assert.True(mkdirResult.Created);
        Assert.Equal(2, mkdirResult.DirectoriesCreated.Count);
        Assert.True(Directory.Exists(targetPath));

        // 3. Stat created folder
        var statResponseAfter = await mcpTools.StatAsync(_deviceId, targetPath);
        Assert.False(statResponseAfter.IsError);
        var textBlockAfter = Assert.IsType<TextContentBlock>(statResponseAfter.Content[0]);
        var statResultAfter = JsonSerializer.Deserialize<StatResult>(textBlockAfter.Text, JsonOptions.Default);
        Assert.NotNull(statResultAfter);
        Assert.True(statResultAfter.Exists);
        Assert.Equal("directory", statResultAfter.Type);
    }

    [Fact]
    public async Task FsBatchStat_EndToEndFlow_ReturnsOrderedMixedResults()
    {
        await InitializeAsync();

        Assert.NotNull(_tempRoot);
        Assert.NotNull(_gatewayApp);

        var filePath = Path.Combine(_tempRoot, "batch-file.txt");
        var directoryPath = Path.Combine(_tempRoot, "batch-directory");
        var missingPath = Path.Combine(_tempRoot, "batch-missing.txt");
        await File.WriteAllTextAsync(filePath, "batch");
        Directory.CreateDirectory(directoryPath);

        var tools = new FileSystemTools(
            _gatewayApp.Services.GetRequiredService<ICommandDispatcher>(),
            _gatewayApp.Services.GetRequiredService<IAuthorizationService>(),
            NullLogger<FileSystemTools>.Instance,
            _gatewayApp.Services.GetService<IHttpContextAccessor>());

        var response = await tools.BatchStatAsync(
            _deviceId,
            new List<string> { directoryPath, missingPath, filePath });

        Assert.False(response.IsError);
        var textBlock = Assert.IsType<TextContentBlock>(response.Content[0]);
        var data = JsonSerializer.Deserialize<BatchStatResult>(textBlock.Text, JsonOptions.Default);

        Assert.NotNull(data);
        Assert.Equal(3, data.Succeeded);
        Assert.Equal(0, data.Failed);
        Assert.Equal(new[] { directoryPath, missingPath, filePath }, data.Items.Select(item => item.Path).ToArray());
        Assert.Equal("directory", data.Items[0].Data!.Type);
        Assert.False(data.Items[1].Data!.Exists);
        Assert.Equal("file", data.Items[2].Data!.Type);
    }

    [Fact]
    public async Task FsMove_EndToEndFlow_MovesFileSuccessfully()
    {
        await InitializeAsync();

        Assert.NotNull(_tempRoot);
        Assert.NotNull(_gatewayApp);

        var srcFile = Path.Combine(_tempRoot, "move_src.txt");
        var dstFile = Path.Combine(_tempRoot, "move_dst.txt");
        const string text = "Move test content";
        await File.WriteAllTextAsync(srcFile, text);

        var mcpTools = new FileSystemTools(
            _gatewayApp.Services.GetRequiredService<ICommandDispatcher>(),
            _gatewayApp.Services.GetRequiredService<IAuthorizationService>(),
            NullLogger<FileSystemTools>.Instance,
            _gatewayApp.Services.GetService<IHttpContextAccessor>()
        );

        // Call fs_move
        var response = await mcpTools.MoveAsync(_deviceId, srcFile, dstFile, overwrite: false, expectedSha256: null);

        Assert.False(response.IsError);
        Assert.NotNull(response.Content);
        Assert.Single(response.Content);

        var textBlock = Assert.IsType<TextContentBlock>(response.Content[0]);
        var data = JsonSerializer.Deserialize<MoveResult>(textBlock.Text, JsonOptions.Default);

        Assert.NotNull(data);
        Assert.Equal(dstFile, data.Path);
        Assert.False(data.IsDirectory);
        Assert.False(File.Exists(srcFile));
        Assert.True(File.Exists(dstFile));
        Assert.Equal(text, await File.ReadAllTextAsync(dstFile));
    }

    [Fact]
    public async Task FsCopy_EndToEndFlow_CopiesFileSuccessfully()
    {
        await InitializeAsync();

        Assert.NotNull(_tempRoot);
        Assert.NotNull(_gatewayApp);

        var srcFile = Path.Combine(_tempRoot, "copy_src.txt");
        var dstFile = Path.Combine(_tempRoot, "copy_dst.txt");
        const string text = "Copy test content";
        await File.WriteAllTextAsync(srcFile, text);

        var mcpTools = new FileSystemTools(
            _gatewayApp.Services.GetRequiredService<ICommandDispatcher>(),
            _gatewayApp.Services.GetRequiredService<IAuthorizationService>(),
            NullLogger<FileSystemTools>.Instance,
            _gatewayApp.Services.GetService<IHttpContextAccessor>()
        );

        // Call fs_copy
        var response = await mcpTools.CopyAsync(_deviceId, srcFile, dstFile, overwrite: false, expectedSourceSha256: null);

        Assert.False(response.IsError);
        Assert.NotNull(response.Content);
        Assert.Single(response.Content);

        var textBlock = Assert.IsType<TextContentBlock>(response.Content[0]);
        var data = JsonSerializer.Deserialize<CopyResult>(textBlock.Text, JsonOptions.Default);

        Assert.NotNull(data);
        Assert.Equal(dstFile, data.Path);
        Assert.Equal(text.Length, data.BytesCopied);
        Assert.True(File.Exists(srcFile));
        Assert.True(File.Exists(dstFile));
        Assert.Equal(text, await File.ReadAllTextAsync(dstFile));
    }

    [Fact]
    public async Task FsCopy_EndToEndFlow_CopiesDirectorySuccessfully()
    {
        await InitializeAsync();

        Assert.NotNull(_tempRoot);
        Assert.NotNull(_gatewayApp);

        var source = Path.Combine(_tempRoot, "copy-dir-source");
        var destination = Path.Combine(_tempRoot, "copy-dir-destination");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "root.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "child.txt"), "child");

        var tools = new FileSystemTools(
            _gatewayApp.Services.GetRequiredService<ICommandDispatcher>(),
            _gatewayApp.Services.GetRequiredService<IAuthorizationService>(),
            NullLogger<FileSystemTools>.Instance,
            _gatewayApp.Services.GetService<IHttpContextAccessor>());

        var response = await tools.CopyAsync(
            _deviceId,
            source,
            destination,
            overwrite: false,
            expectedSourceSha256: null,
            recursive: true,
            maxEntries: 100,
            maxTotalBytes: 1024);

        Assert.False(response.IsError);
        var textBlock = Assert.IsType<TextContentBlock>(response.Content[0]);
        var data = JsonSerializer.Deserialize<CopyResult>(textBlock.Text, JsonOptions.Default);

        Assert.NotNull(data);
        Assert.True(data.IsDirectory);
        Assert.Equal(2, data.FilesCopied);
        Assert.Equal(2, data.DirectoriesCreated);
        Assert.True(File.Exists(Path.Combine(destination, "nested", "child.txt")));
        Assert.True(Directory.Exists(source));
    }

    [Fact]
    public async Task FsRmdir_EndToEndFlow_RemovesEmptyDirectorySuccessfully()
    {
        await InitializeAsync();

        Assert.NotNull(_tempRoot);
        Assert.NotNull(_gatewayApp);

        var directoryPath = Path.Combine(_tempRoot, "remove-empty-directory");
        Directory.CreateDirectory(directoryPath);

        var tools = new FileSystemTools(
            _gatewayApp.Services.GetRequiredService<ICommandDispatcher>(),
            _gatewayApp.Services.GetRequiredService<IAuthorizationService>(),
            NullLogger<FileSystemTools>.Instance,
            _gatewayApp.Services.GetService<IHttpContextAccessor>());

        var response = await tools.RemoveDirectoryAsync(_deviceId, directoryPath, missingOk: false);

        Assert.False(response.IsError);
        var textBlock = Assert.IsType<TextContentBlock>(response.Content[0]);
        var data = JsonSerializer.Deserialize<RemoveDirectoryResult>(textBlock.Text, JsonOptions.Default);

        Assert.NotNull(data);
        Assert.Equal(directoryPath, data.Path);
        Assert.True(data.Removed);
        Assert.False(Directory.Exists(directoryPath));
    }

    [Fact]
    public async Task FsDelete_EndToEndFlow_RemovesFileSuccessfully()
    {
        await InitializeAsync();

        Assert.NotNull(_tempRoot);
        Assert.NotNull(_gatewayApp);

        var filePath = Path.Combine(_tempRoot, "remove-test.txt");
        const string text = "Remove test content";
        await File.WriteAllTextAsync(filePath, text);

        var tools = new FileSystemTools(
            _gatewayApp.Services.GetRequiredService<ICommandDispatcher>(),
            _gatewayApp.Services.GetRequiredService<IAuthorizationService>(),
            NullLogger<FileSystemTools>.Instance,
            _gatewayApp.Services.GetService<IHttpContextAccessor>());

        var response = await tools.DeleteAsync(_deviceId, filePath, expectedSha256: null, missingOk: false);

        Assert.False(response.IsError);
        var textBlock = Assert.IsType<TextContentBlock>(response.Content[0]);
        var data = JsonSerializer.Deserialize<DeleteResult>(textBlock.Text, JsonOptions.Default);

        Assert.NotNull(data);
        Assert.Equal(filePath, data.Path);
        Assert.Equal((long)text.Length, data.BytesDeleted);
        Assert.NotNull(data.Sha256);
        Assert.False(File.Exists(filePath));
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
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
