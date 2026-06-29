using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LocalMcp.Agent.Windows.Connection;
using LocalMcp.Agent.Windows.ProcessControl;
using LocalMcp.Agent.Windows.UiAutomation;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.BuildingBlocks.Serialization;
using LocalMcp.Contracts.Commands;
using LocalMcp.Gateway.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace LocalMcp.UnitTests;

public sealed class PhaseFourTests
{
    public static TheoryData<Type, string, bool, bool, bool, string[]> ToolSchemas => new()
    {
        {
            typeof(UiTextReadTools),
            "ui_get_text",
            true,
            false,
            true,
            ["deviceId", "windowHandle", "scope", "automationId", "name", "controlType", "occurrenceIndex", "startLine", "lineCount", "maxCharacters", "focusWindow"]
        },
        {
            typeof(ClipboardTools),
            "clipboard_get",
            true,
            false,
            true,
            ["deviceId", "maxCharacters"]
        },
        {
            typeof(ClipboardTools),
            "clipboard_set",
            false,
            true,
            true,
            ["deviceId", "text", "verify"]
        },
        {
            typeof(UiKeyboardTools),
            "ui_hotkey",
            false,
            true,
            false,
            ["deviceId", "windowHandle", "keys", "automationId", "name", "controlType", "occurrenceIndex", "focusWindow"]
        },
        {
            typeof(ProcessTools),
            "process_list",
            true,
            false,
            true,
            ["deviceId", "nameContains", "includeWindowless", "maxResults"]
        },
        {
            typeof(ProcessTools),
            "process_kill",
            false,
            true,
            false,
            ["deviceId", "processId", "expectedProcessName", "entireProcessTree", "timeoutMs"]
        },
        {
            typeof(FileDialogTools),
            "file_dialog_set_path",
            false,
            true,
            false,
            ["deviceId", "windowHandle", "path", "automationId", "name", "controlType", "occurrenceIndex", "focusWindow", "submit"]
        }
    };

    [Theory]
    [MemberData(nameof(ToolSchemas))]
    public void Tool_HasExpectedMetadataAndSchema(
        Type toolType,
        string toolName,
        bool readOnly,
        bool destructive,
        bool idempotent,
        string[] parameterNames)
    {
        var method = toolType.GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(readOnly, attribute!.ReadOnly);
        Assert.Equal(destructive, attribute.Destructive);
        Assert.Equal(idempotent, attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
        Assert.Equal(parameterNames, method.GetParameters().Select(parameter => parameter.Name).ToArray());
    }

    [Fact]
    public void NewCommands_KeepBoundedDefaults()
    {
        var common = new
        {
            CommandId = Guid.NewGuid(),
            DeviceId = "dev",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var clipboardGet = new ClipboardGetCommand
        {
            CommandId = common.CommandId,
            DeviceId = common.DeviceId,
            CreatedAt = common.CreatedAt
        };
        var clipboardSet = new ClipboardSetCommand
        {
            CommandId = common.CommandId,
            DeviceId = common.DeviceId,
            CreatedAt = common.CreatedAt,
            Text = string.Empty
        };
        var processList = new ProcessListCommand
        {
            CommandId = common.CommandId,
            DeviceId = common.DeviceId,
            CreatedAt = common.CreatedAt
        };
        var processKill = new ProcessKillCommand
        {
            CommandId = common.CommandId,
            DeviceId = common.DeviceId,
            CreatedAt = common.CreatedAt,
            ProcessId = 42
        };
        var fileDialog = new FileDialogSetPathCommand
        {
            CommandId = common.CommandId,
            DeviceId = common.DeviceId,
            CreatedAt = common.CreatedAt,
            WindowHandle = "0x1234",
            Path = "C:\\temp\\file.txt"
        };

        Assert.Equal(65_536, clipboardGet.MaxCharacters);
        Assert.True(clipboardSet.Verify);
        Assert.True(processList.IncludeWindowless);
        Assert.Equal(200, processList.MaxResults);
        Assert.True(processKill.EntireProcessTree);
        Assert.Equal(5_000, processKill.TimeoutMs);
        Assert.True(fileDialog.FocusWindow);
        Assert.False(fileDialog.Submit);
        Assert.Equal(TimeSpan.FromSeconds(15), AgentCommandTimeouts.GetTimeout(processKill));
    }

    [Theory]
    [InlineData(typeof(ClipboardGetCommand))]
    [InlineData(typeof(ClipboardSetCommand))]
    [InlineData(typeof(ProcessListCommand))]
    [InlineData(typeof(ProcessKillCommand))]
    [InlineData(typeof(FileDialogSetPathCommand))]
    public void AgentDeserializer_RecognizesPhaseFourCommands(Type commandType)
    {
        var command = CreateCommand(commandType);
        var json = JsonSerializer.Serialize(command, commandType, JsonOptions.Default);
        var deserialized = DeserializeExtended(commandType.Name, json);

        Assert.NotNull(deserialized);
        Assert.IsType(commandType, deserialized);
    }

    [Fact]
    public async Task ProcessList_IncludesCurrentTestProcess()
    {
        var manager = new ProcessManager();
        var result = await manager.ListAsync(
            nameContains: null,
            includeWindowless: true,
            maxResults: 1_000,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.Contains(result.Data!.Processes, item => item.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public async Task ProcessKill_TerminatesDisposableProcessTree()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /c ping.exe -n 30 127.0.0.1 > nul",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);

        try
        {
            await Task.Delay(150);
            var manager = new ProcessManager();
            var result = await manager.KillAsync(
                process!.Id,
                "cmd.exe",
                entireProcessTree: true,
                timeoutMs: 5_000,
                Guid.NewGuid(),
                CancellationToken.None);

            Assert.True(result.Success, result.Error?.Message);
            Assert.True(result.Data!.KillRequested);
            Assert.True(result.Data.Exited);
            Assert.True(process.WaitForExit(2_000));
        }
        finally
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task ProcessKill_RefusesCurrentAgentProcess()
    {
        var manager = new ProcessManager();
        var result = await manager.KillAsync(
            Environment.ProcessId,
            Process.GetCurrentProcess().ProcessName,
            entireProcessTree: false,
            timeoutMs: 1_000,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ProcessKillProtected, result.Error?.Code);
    }

    [Fact]
    public async Task FileDialogSetPath_OnInteractiveCommonDialog_Succeeds()
    {
        if (!OperatingSystem.IsWindows()
            || !string.Equals(
                Environment.GetEnvironmentVariable("LOCALMCP_INTERACTIVE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var title = $"AgentBridge Phase 4 Dialog {Guid.NewGuid():N}";
        var script = string.Join(
            ';',
            "Add-Type -AssemblyName System.Windows.Forms",
            "$dialog = [System.Windows.Forms.OpenFileDialog]::new()",
            $"$dialog.Title = '{title}'",
            "$dialog.CheckFileExists = $false",
            "[void]$dialog.ShowDialog()");
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Sta");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        try
        {
            var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
            var wait = await executor.WaitForWindowAsync(
                windowHandle: null,
                processId: process!.Id,
                processName: null,
                className: null,
                title,
                titleContains: null,
                occurrenceIndex: 0,
                condition: WindowWaitConditions.Exists,
                expectedTitle: null,
                includeInvisible: false,
                timeoutMs: 10_000,
                pollIntervalMs: 100,
                Guid.NewGuid(),
                CancellationToken.None);

            Assert.True(wait.Success, wait.Error?.Message);
            Assert.NotNull(wait.Data?.Window);

            const string path = @"C:\Windows\notepad.exe";
            var result = await executor.FileDialogSetPathAsync(
                wait.Data!.Window!.WindowHandle,
                path,
                automationId: null,
                name: null,
                controlType: null,
                occurrenceIndex: 0,
                focusWindow: true,
                submit: false,
                Guid.NewGuid(),
                CancellationToken.None);

            Assert.True(result.Success, result.Error?.Message);
            Assert.True(result.Data!.Verified);
            Assert.False(result.Data.Submitted);
            Assert.Equal(path.Length, result.Data.PathLength);
        }
        finally
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task ClipboardGet_ReadsCurrentClipboardWithoutMutation()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var result = await executor.ClipboardGetAsync(
            65_536,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.InRange(result.Data!.ReturnedCharacters, 0, 65_536);
    }

    [Fact]
    public async Task ClipboardSet_RoundTripsTextAndRestoresOriginalWhenClipboardIsTextOnly()
    {
        if (!OperatingSystem.IsWindows() || !ClipboardContainsOnlyTextFormats())
            return;

        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);
        var original = await executor.ClipboardGetAsync(
            1_048_576,
            Guid.NewGuid(),
            CancellationToken.None);
        if (!original.Success
            || original.Data is not { HasText: true, CharacterCountExact: true, Truncated: false }
            || original.Data.Text is null)
        {
            return;
        }

        var marker = $"AgentBridge Phase 4 clipboard {Guid.NewGuid():N} ✓";
        try
        {
            var set = await executor.ClipboardSetAsync(marker, true, Guid.NewGuid(), CancellationToken.None);
            var get = await executor.ClipboardGetAsync(1_048_576, Guid.NewGuid(), CancellationToken.None);

            Assert.True(set.Success, set.Error?.Message);
            Assert.True(set.Data!.Verified);
            Assert.True(get.Success, get.Error?.Message);
            Assert.Equal(marker, get.Data!.Text);
        }
        finally
        {
            var restore = await executor.ClipboardSetAsync(
                original.Data.Text,
                true,
                Guid.NewGuid(),
                CancellationToken.None);
            Assert.True(restore.Success, restore.Error?.Message);
        }
    }

    [Fact]
    public async Task ClipboardAndDialogValidation_RejectUnsafeInputsBeforeWindowsCalls()
    {
        var executor = new UiAutomationExecutor(NullLogger<UiAutomationExecutor>.Instance);

        var clipboardGet = await executor.ClipboardGetAsync(0, Guid.NewGuid(), CancellationToken.None);
        var clipboardSet = await executor.ClipboardSetAsync("bad\0text", true, Guid.NewGuid(), CancellationToken.None);
        var dialog = await executor.FileDialogSetPathAsync(
            "0x1234",
            "bad\0path",
            null,
            null,
            null,
            0,
            true,
            false,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(ErrorCodes.InvalidRequest, clipboardGet.Error?.Code);
        Assert.Equal(ErrorCodes.InvalidRequest, clipboardSet.Error?.Code);
        Assert.Equal(ErrorCodes.InvalidRequest, dialog.Error?.Code);
    }

    private static bool ClipboardContainsOnlyTextFormats()
    {
        var opened = false;
        for (var attempt = 0; attempt < 10 && !opened; attempt++)
        {
            opened = OpenClipboard(IntPtr.Zero);
            if (!opened)
                Thread.Sleep(20);
        }
        if (!opened)
            return false;

        try
        {
            var foundAny = false;
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                foundAny = true;
                if (format is not 1 and not 7 and not 13 and not 16)
                    return false;
            }
            return foundAny;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static AgentCommand CreateCommand(Type commandType)
    {
        var commandId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        return commandType.Name switch
        {
            nameof(ClipboardGetCommand) => new ClipboardGetCommand
            {
                CommandId = commandId,
                DeviceId = "dev",
                CreatedAt = createdAt,
                MaxCharacters = 123
            },
            nameof(ClipboardSetCommand) => new ClipboardSetCommand
            {
                CommandId = commandId,
                DeviceId = "dev",
                CreatedAt = createdAt,
                Text = "hello",
                Verify = false
            },
            nameof(ProcessListCommand) => new ProcessListCommand
            {
                CommandId = commandId,
                DeviceId = "dev",
                CreatedAt = createdAt,
                NameContains = "note",
                IncludeWindowless = false,
                MaxResults = 12
            },
            nameof(ProcessKillCommand) => new ProcessKillCommand
            {
                CommandId = commandId,
                DeviceId = "dev",
                CreatedAt = createdAt,
                ProcessId = 42,
                ExpectedProcessName = "notepad.exe",
                EntireProcessTree = false,
                TimeoutMs = 1234
            },
            nameof(FileDialogSetPathCommand) => new FileDialogSetPathCommand
            {
                CommandId = commandId,
                DeviceId = "dev",
                CreatedAt = createdAt,
                WindowHandle = "0x1234",
                Path = "C:\\temp\\file.txt",
                AutomationId = "1148",
                ControlType = "Edit",
                OccurrenceIndex = 1,
                FocusWindow = false,
                Submit = true
            },
            _ => throw new ArgumentOutOfRangeException(nameof(commandType))
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    private static AgentCommand? DeserializeExtended(string commandType, string json)
    {
        var method = typeof(GatewayConnection).GetMethod(
            "DeserializeExtendedCommand",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<AgentCommand?>(method.Invoke(null, [commandType, json]));
    }
}
