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
    private SingleInstanceGuard? _singleInstanceGuard;
    private bool _isExiting;
    private bool _servicesStopped;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!SingleInstanceGuard.TryAcquire(
                () => Dispatcher.BeginInvoke(ShowMainWindow),
                out _singleInstanceGuard))
        {
            await DesktopLog.WriteAsync(
                "A second AgentBridge Desktop launch was blocked by the single-instance guard.");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0xF4, 0x7C, 0x20),
            ApplicationTheme.Dark,
            systemGlassColor: false,
            systemAccentColor: false);

        var c12 = System.Windows.Media.Color.FromRgb(0x12, 0x12, 0x12);
        var c1a = System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A);
        var c18 = System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x18);
        var c20 = System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20);
        var c28 = System.Windows.Media.Color.FromRgb(0x28, 0x28, 0x28);

        // WPF UI Color Keys
        Resources["CardBackgroundFillColorDefault"] = c12;
        Resources["CardBackgroundFillColorSecondary"] = c12;
        Resources["CardBackgroundFillColorTertiary"] = c12;
        Resources["LayerFillColorDefault"] = c12;
        Resources["LayerFillColorAlt"] = c12;
        Resources["LayerOnAcrylicFillColorDefault"] = c12;
        Resources["SolidBackgroundFillColorBase"] = c12;
        Resources["SolidBackgroundFillColorSecondary"] = c12;
        Resources["SolidBackgroundFillColorTertiary"] = c12;
        Resources["ControlFillColorDefault"] = c12;
        Resources["ControlFillColorSecondary"] = c18;
        Resources["ControlFillColorTertiary"] = c20;
        Resources["SubtleFillColorSecondary"] = c18;
        Resources["SubtleFillColorTertiary"] = c20;
        Resources["CardStrokeColorDefault"] = c28;
        Resources["CardStrokeColorDefaultSolid"] = c28;

        // WPF UI Brush Keys
        Resources["AgentBridgeBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(c12);
        Resources["AgentBridgeSidebarBrush"] = new System.Windows.Media.SolidColorBrush(c1a);
        Resources["AgentBridgeSurfaceBrush"] = new System.Windows.Media.SolidColorBrush(c12);
        Resources["AgentBridgeAccentSoftBrush"] = new System.Windows.Media.SolidColorBrush(c18);
        Resources["AgentBridgeBorderBrush"] = new System.Windows.Media.SolidColorBrush(c28);
        Resources["CardBackgroundFillColorDefaultBrush"] = new System.Windows.Media.SolidColorBrush(c12);
        Resources["CardBackgroundFillColorSecondaryBrush"] = new System.Windows.Media.SolidColorBrush(c12);
        Resources["CardBackgroundFillColorTertiaryBrush"] = new System.Windows.Media.SolidColorBrush(c12);
        Resources["LayerFillColorDefaultBrush"] = new System.Windows.Media.SolidColorBrush(c12);
        Resources["LayerFillColorAltBrush"] = new System.Windows.Media.SolidColorBrush(c12);
        Resources["LayerOnAcrylicFillColorDefaultBrush"] = new System.Windows.Media.SolidColorBrush(c12);
        Resources["SolidBackgroundFillColorBaseBrush"] = new System.Windows.Media.SolidColorBrush(c12);
        Resources["SolidBackgroundFillColorSecondaryBrush"] = new System.Windows.Media.SolidColorBrush(c12);
        Resources["SolidBackgroundFillColorTertiaryBrush"] = new System.Windows.Media.SolidColorBrush(c12);
        Resources["ControlFillColorDefaultBrush"] = new System.Windows.Media.SolidColorBrush(c12);
        Resources["ControlFillColorSecondaryBrush"] = new System.Windows.Media.SolidColorBrush(c18);
        Resources["ControlFillColorTertiaryBrush"] = new System.Windows.Media.SolidColorBrush(c20);
        Resources["SubtleFillColorSecondaryBrush"] = new System.Windows.Media.SolidColorBrush(c18);
        Resources["SubtleFillColorTertiaryBrush"] = new System.Windows.Media.SolidColorBrush(c20);
        Resources["CardStrokeColorDefaultBrush"] = new System.Windows.Media.SolidColorBrush(c28);

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
        _singleInstanceGuard?.Dispose();
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
            "Gateway, Windows Agent, and Tunnel continue running in the background.",
            Forms.ToolTipIcon.Info);

        TrimMemory();
    }

    private static void TrimMemory()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            using var process = System.Diagnostics.Process.GetCurrentProcess();
            _ = NativeMethods.EmptyWorkingSet(process.Handle);
        }
        catch
        {
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EmptyWorkingSet(IntPtr hProcess);
    }

    private void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_mainWindow is null)
                return;

            _mainWindow.ShowInTaskbar = true;
            _mainWindow.Visibility = Visibility.Visible;
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;

            _mainWindow.Show();
            _mainWindow.Activate();
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
            _mainWindow.Focus();
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
