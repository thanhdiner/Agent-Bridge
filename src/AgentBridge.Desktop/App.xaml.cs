using System;
using System.ComponentModel;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AgentBridge.Desktop.Services;
using Wpf.Ui.Appearance;
using Forms = System.Windows.Forms;

namespace AgentBridge.Desktop;

public partial class App : System.Windows.Application
{
    private readonly ServiceSupervisor _supervisor = new();
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _applicationIcon;
    private MainWindow? _mainWindow;
    private bool _isExiting;
    private bool _servicesStopped;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x8A, 0x7C, 0xFF),
            ApplicationTheme.Dark,
            systemGlassColor: false,
            systemAccentColor: false);

        _mainWindow = new MainWindow(_supervisor);
        _mainWindow.Closing += OnMainWindowClosing;
        _mainWindow.Show();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open AgentBridge", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Restart services", null, async (_, _) => await RestartServicesFromTrayAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _applicationIcon = LoadApplicationIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "AgentBridge",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        try
        {
            await _supervisor.StartAsync();
        }
        catch (Exception ex)
        {
            await DesktopLog.WriteAsync("Unhandled supervisor startup failure.", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_servicesStopped)
        {
            try
            {
                _supervisor.StopAsync().GetAwaiter().GetResult();
                _servicesStopped = true;
            }
            catch (Exception ex)
            {
                DesktopLog.WriteAsync("Failed to stop managed services during exit.", ex)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _applicationIcon?.Dispose();
        base.OnExit(e);
    }

    private static Icon LoadApplicationIcon()
    {
        var resource = GetResourceStream(
            new Uri("pack://application:,,,/Assets/agentbridge.ico", UriKind.Absolute));
        if (resource is not null)
        {
            using var stream = resource.Stream;
            using var icon = new Icon(stream);
            return (Icon)icon.Clone();
        }

        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            var extracted = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            if (extracted is not null)
                return extracted;
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
            return;

        e.Cancel = true;
        _mainWindow?.Hide();
        _trayIcon?.ShowBalloonTip(
            1800,
            "AgentBridge is still running",
            "Gateway and Windows Agent continue running in the background.",
            Forms.ToolTipIcon.Info);
    }

    private void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_mainWindow is null)
                return;

            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;

            _mainWindow.Activate();
        });
    }

    private async Task RestartServicesFromTrayAsync()
    {
        try
        {
            await _supervisor.RestartAsync();
            ShowMainWindow();
        }
        catch (Exception ex)
        {
            await DesktopLog.WriteAsync("Tray service restart failed.", ex);
        }
    }

    private async void ExitApplication()
    {
        if (_isExiting)
            return;

        _isExiting = true;
        try
        {
            await _supervisor.StopAsync();
            _servicesStopped = true;
        }
        catch (Exception ex)
        {
            await DesktopLog.WriteAsync("Failed to stop managed services.", ex);
        }

        _mainWindow?.Close();
        Shutdown();
    }

    private async void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        await DesktopLog.WriteAsync("Unhandled UI exception.", e.Exception);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        DesktopLog.WriteAsync(
                "Unhandled AppDomain exception.",
                e.ExceptionObject as Exception)
            .GetAwaiter()
            .GetResult();
    }

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        DesktopLog.WriteAsync("Unobserved task exception.", e.Exception)
            .GetAwaiter()
            .GetResult();
    }
}
