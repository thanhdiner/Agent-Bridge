using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentBridge.Desktop.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace AgentBridge.Desktop;

public partial class AndroidSetupPage : UserControl, IDisposable
{
    private readonly Func<string> _gatewayUrl;
    private readonly AndroidAdbSettingsStore _settingsStore = new();
    private readonly AndroidAdbService _adb = new();
    private readonly AndroidAgentProcessManager _agent = new();
    private bool _loaded;
    private bool _busy;
    private string? _currentSerial;

    public AndroidSetupPage(Func<string> gatewayUrl)
    {
        _gatewayUrl = gatewayUrl ?? throw new ArgumentNullException(nameof(gatewayUrl));
        InitializeComponent();
        ConfigPathText.Text = _settingsStore.ConfigurationPath;
        Loaded += AndroidSetupPage_Loaded;
        _agent.StateChanged += Agent_StateChanged;
        UpdateAgentStatus();
    }

    public async Task ActivateAsync()
    {
        if (!_loaded)
            await LoadSettingsAsync();
        await RefreshAsync(showSuccess: false);
    }

    public void Dispose()
    {
        _agent.StateChanged -= Agent_StateChanged;
        _agent.Dispose();
    }

    private async void AndroidSetupPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= AndroidSetupPage_Loaded;
        await ActivateAsync();
    }

    private async Task LoadSettingsAsync()
    {
        var settings = await _settingsStore.LoadAsync();
        AdbPathTextBox.Text = settings.AdbPath;
        DeviceIpTextBox.Text = settings.DeviceIp;
        PairingPortTextBox.Text = settings.PairingPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ConnectionPortTextBox.Text = settings.ConnectionPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        var resolved = _adb.ResolveAdbPath(settings.AdbPath);
        if (resolved is not null)
            AdbPathTextBox.Text = resolved;
        _loaded = true;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(showSuccess: true);

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            var adbPath = RequireAdbPath();
            var ip = RequireIp();
            var port = RequirePort(PairingPortTextBox.Text, "pairing port");
            var code = PairingCodeBox.Password;
            try
            {
                var result = await _adb.PairAsync(adbPath, ip, port, code);
                await SaveSettingsAsync();
                ShowFeedback("Phone paired", result, InfoBarSeverity.Success);
            }
            finally
            {
                PairingCodeBox.Clear();
            }

            await RefreshCoreAsync();
        }, "Pairing failed");
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            var adbPath = RequireAdbPath();
            var ip = RequireIp();
            var port = RequirePort(ConnectionPortTextBox.Text, "connection port");
            var result = await _adb.ConnectAsync(adbPath, ip, port);
            await SaveSettingsAsync();
            await RefreshCoreAsync();
            ShowFeedback("ADB connected", result, InfoBarSeverity.Success);
        }, "Connection failed");
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            _agent.Stop();
            var result = await _adb.DisconnectAsync(
                RequireAdbPath(),
                RequireIp(),
                RequirePort(ConnectionPortTextBox.Text, "connection port"));
            _currentSerial = null;
            await RefreshCoreAsync();
            ShowFeedback("Phone disconnected", result, InfoBarSeverity.Success);
        }, "Disconnect failed");
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            _ = RequireAdbPath();
            if (!string.IsNullOrWhiteSpace(DeviceIpTextBox.Text))
                _ = RequireIp();
            if (!string.IsNullOrWhiteSpace(PairingPortTextBox.Text))
                _ = RequirePort(PairingPortTextBox.Text, "pairing port");
            if (!string.IsNullOrWhiteSpace(ConnectionPortTextBox.Text))
                _ = RequirePort(ConnectionPortTextBox.Text, "connection port");
            await SaveSettingsAsync();
            ShowFeedback("Settings saved", "Pairing code was not stored.", InfoBarSeverity.Success);
        }, "Could not save settings");
    }

    private void BrowseAdb_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select adb.exe",
            Filter = "Android Debug Bridge (adb.exe)|adb.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
            AdbPathTextBox.Text = dialog.FileName;
    }

    private async void StartAgent_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            await RefreshCoreAsync();
            if (string.IsNullOrWhiteSpace(_currentSerial))
                throw new InvalidOperationException("Connect the phone with ADB before starting the Android Agent.");

            await SaveSettingsAsync();
            await _agent.StartAsync(RequireAdbPath(), _currentSerial, _gatewayUrl());
            UpdateAgentStatus();
            ShowFeedback(
                "Android Agent started",
                "The phone is available through the dedicated /mcp/android/a endpoint.",
                InfoBarSeverity.Success);
        }, "Android Agent failed to start");
    }

    private void StopAgent_Click(object sender, RoutedEventArgs e)
    {
        _agent.Stop();
        UpdateAgentStatus();
        ShowFeedback("Android Agent stopped", "ADB pairing and saved settings were kept.", InfoBarSeverity.Informational);
    }

    private async Task RefreshAsync(bool showSuccess)
    {
        await RunActionAsync(async () =>
        {
            await RefreshCoreAsync();
            if (showSuccess)
                ShowFeedback("Android status refreshed", "ADB and the phone connection were checked.", InfoBarSeverity.Success);
        }, "Android status check failed");
    }

    private async Task RefreshCoreAsync()
    {
        var resolved = _adb.ResolveAdbPath(AdbPathTextBox.Text);
        if (resolved is null)
            throw new InvalidOperationException("ADB was not found. Install Android Platform Tools or browse to adb.exe.");
        AdbPathTextBox.Text = resolved;

        var preferredIp = DeviceIpTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(preferredIp))
            _ = AndroidAdbService.BuildEndpoint(preferredIp, 1);
        var preferredPort = TryParsePort(ConnectionPortTextBox.Text);
        var snapshot = await _adb.InspectAsync(resolved, preferredIp, preferredPort);
        ApplySnapshot(snapshot);
        await SaveSettingsAsync();
    }

    private void ApplySnapshot(AndroidAdbSnapshot snapshot)
    {
        AdbStatusText.Text = $"ADB: {snapshot.AdbVersion}";
        AdbStatusText.Foreground = FindBrush("AgentBridgeSuccessBrush");

        if (TrySplitEndpoint(snapshot.DiscoveredPairingEndpoint, out var pairingIp, out var pairingPort))
        {
            if (string.IsNullOrWhiteSpace(DeviceIpTextBox.Text))
                DeviceIpTextBox.Text = pairingIp;
            if (string.IsNullOrWhiteSpace(PairingPortTextBox.Text))
                PairingPortTextBox.Text = pairingPort.ToString(CultureInfo.InvariantCulture);
        }

        if (TrySplitEndpoint(snapshot.DiscoveredConnectionEndpoint, out var connectionIp, out var connectionPort))
        {
            DeviceIpTextBox.Text = connectionIp;
            ConnectionPortTextBox.Text = connectionPort.ToString(CultureInfo.InvariantCulture);
        }

        if (snapshot.Connected && snapshot.Serial is not null)
        {
            _currentSerial = snapshot.Serial;
            if (TrySplitEndpoint(snapshot.Serial, out var serialIp, out var serialPort))
            {
                DeviceIpTextBox.Text = serialIp;
                ConnectionPortTextBox.Text = serialPort.ToString(CultureInfo.InvariantCulture);
            }

            DeviceStatusText.Text = $"Phone: connected ({snapshot.Serial})";
            DeviceStatusText.Foreground = FindBrush("AgentBridgeSuccessBrush");
            var model = string.Join(' ', new[] { snapshot.Manufacturer, snapshot.Model }).Trim();
            DeviceDetailText.Text = $"{(string.IsNullOrWhiteSpace(model) ? "Android device" : model)}  ·  Android {ValueOrDash(snapshot.AndroidVersion)}  ·  {ValueOrDash(snapshot.ScreenSize)}";
            DebugFlagsText.Text = snapshot.DebugFlags;
            OverallStatusBorder.Background = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));
            OverallStatusText.Text = _agent.IsRunning ? "Phone + agent online" : "Phone connected";
        }
        else
        {
            _currentSerial = null;
            DeviceStatusText.Text = snapshot.Devices.Count == 0
                ? "Phone: disconnected"
                : "Phone: ADB device is not ready";
            DeviceStatusText.Foreground = FindBrush("AgentBridgeWarningBrush");
            DeviceDetailText.Text = snapshot.Devices.Count == 0
                ? "Open Wireless debugging, confirm the connection port, then select Connect ADB."
                : string.Join(Environment.NewLine, snapshot.Devices.Select(device => $"{device.Serial}: {device.State}"));
            DebugFlagsText.Text = string.Empty;
            OverallStatusBorder.Background = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
            OverallStatusText.Text = "Waiting for phone";
        }

        UpdateAgentStatus();
    }

    private async Task SaveSettingsAsync()
    {
        var resolved = _adb.ResolveAdbPath(AdbPathTextBox.Text) ?? AdbPathTextBox.Text.Trim();
        await _settingsStore.SaveAsync(new AndroidAdbSettings(
            resolved,
            DeviceIpTextBox.Text.Trim(),
            TryParsePort(PairingPortTextBox.Text),
            TryParsePort(ConnectionPortTextBox.Text)));
    }

    private async Task RunActionAsync(Func<Task> action, string failureTitle)
    {
        if (_busy)
            return;
        _busy = true;
        SetButtonsEnabled(false);
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is ArgumentException
                                      or InvalidOperationException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or TimeoutException)
        {
            await DesktopLog.WriteAsync(failureTitle, ex);
            ShowFeedback(failureTitle, ex.Message, InfoBarSeverity.Error);
            OverallStatusBorder.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            OverallStatusText.Text = "Needs attention";
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
            UpdateAgentStatus();
        }
    }

    private string RequireAdbPath()
    {
        var resolved = _adb.ResolveAdbPath(AdbPathTextBox.Text);
        if (resolved is null)
            throw new InvalidOperationException("ADB was not found. Install Android Platform Tools or browse to adb.exe.");
        AdbPathTextBox.Text = resolved;
        return resolved;
    }

    private string RequireIp()
    {
        var ip = DeviceIpTextBox.Text.Trim();
        _ = AndroidAdbService.BuildEndpoint(ip, 1);
        return ip;
    }

    private static int RequirePort(string value, string label)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new ArgumentException($"Enter a valid {label} between 1 and 65535.");
        return port;
    }

    private static int? TryParsePort(string value) =>
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : null;

    private static bool TrySplitEndpoint(string? endpoint, out string ip, out int port)
    {
        ip = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || separator == endpoint.Length - 1)
            return false;
        ip = endpoint[..separator];
        return int.TryParse(endpoint[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        PairButton.IsEnabled = enabled;
        ConnectButton.IsEnabled = enabled;
        DisconnectButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        StartAgentButton.IsEnabled = enabled;
        StopAgentButton.IsEnabled = enabled && _agent.IsRunning;
    }

    private void Agent_StateChanged()
    {
        if (Dispatcher.HasShutdownStarted)
            return;
        _ = Dispatcher.BeginInvoke(UpdateAgentStatus);
    }

    private void UpdateAgentStatus()
    {
        if (_agent.IsRunning)
        {
            AgentStatusText.Text = $"Running (PID {_agent.ProcessId})";
            AgentStatusText.Foreground = FindBrush("AgentBridgeSuccessBrush");
            StopAgentButton.IsEnabled = !_busy;
            if (_currentSerial is not null)
            {
                OverallStatusBorder.Background = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));
                OverallStatusText.Text = "Phone + agent online";
            }
        }
        else
        {
            AgentStatusText.Text = "Stopped";
            AgentStatusText.Foreground = FindBrush("AgentBridgeMutedBrush");
            StopAgentButton.IsEnabled = false;
        }
    }

    private void ShowFeedback(string title, string message, InfoBarSeverity severity)
    {
        FeedbackInfoBar.Title = title;
        FeedbackInfoBar.Message = message;
        FeedbackInfoBar.Severity = severity;
        FeedbackInfoBar.IsOpen = true;
    }

    private Brush FindBrush(string key) => (Brush)FindResource(key);

    private static string ValueOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
