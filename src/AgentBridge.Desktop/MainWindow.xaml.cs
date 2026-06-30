using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentBridge.Desktop.Services;
using LocalMcp.BuildingBlocks.Configuration;
using Wpf.Ui.Controls;

namespace AgentBridge.Desktop;

public partial class MainWindow : FluentWindow
{
    private readonly ServiceSupervisor _supervisor;
    private readonly UpdateService _updateService = new();
    private readonly DeviceSelectionService _deviceSelectionService = new();
    private readonly LocalWorkspaceConfigurationStore _store = new();
    private readonly ObservableCollection<WorkspaceConfigurationEntry> _workspaces = [];
    private readonly ObservableCollection<DeviceChoice> _deviceChoices = [];
    private bool _isCheckingForUpdates;
    private bool _isRefreshingDevices;
    private bool _ignoreDeviceSelection;
    private readonly DispatcherTimer _feedbackTimer = new()
    {
        Interval = TimeSpan.FromSeconds(4)
    };

    public MainWindow(ServiceSupervisor supervisor)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        InitializeComponent();
        WorkspaceList.ItemsSource = _workspaces;
        DefaultDeviceComboBox.ItemsSource = _deviceChoices;
        ConfigPathText.Text = _store.ConfigurationPath;
        LogsDirectoryText.Text = _supervisor.Current.LogsDirectory;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        _feedbackTimer.Tick += FeedbackTimer_Tick;
        _supervisor.SnapshotChanged += OnSupervisorSnapshotChanged;
        ShowOverview();
        ApplySupervisorSnapshot(_supervisor.Current);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await ReloadAsync();
        await RefreshDeviceSelectionAsync(showErrors: false);
        _ = CheckForUpdatesAsync(isManual: false);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _supervisor.SnapshotChanged -= OnSupervisorSnapshotChanged;
    }

    private void OverviewNav_Click(object sender, RoutedEventArgs e) => ShowOverview();

    private void WorkspacesNav_Click(object sender, RoutedEventArgs e) => ShowWorkspaces();

    private void ShowOverview()
    {
        OverviewPage.Visibility = Visibility.Visible;
        WorkspacePage.Visibility = Visibility.Collapsed;
        OverviewNavButton.Appearance = ControlAppearance.Primary;
        WorkspacesNavButton.Appearance = ControlAppearance.Transparent;
    }

    private void ShowWorkspaces()
    {
        OverviewPage.Visibility = Visibility.Collapsed;
        WorkspacePage.Visibility = Visibility.Visible;
        OverviewNavButton.Appearance = ControlAppearance.Transparent;
        WorkspacesNavButton.Appearance = ControlAppearance.Primary;
    }

    private async void RestartServices_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetServiceButtonsEnabled(false);
            ShowOverviewFeedback(
                "Restarting services",
                "Gateway and Windows Agent are being restarted.",
                InfoBarSeverity.Informational,
                autoClose: false);
            await _supervisor.RestartAsync();
            ApplySupervisorSnapshot(_supervisor.Current);
            ShowOverviewFeedback(
                _supervisor.Current.Gateway.IsHealthy && _supervisor.Current.Agent.IsHealthy
                    ? "Services restarted"
                    : "Restart needs attention",
                _supervisor.Current.Gateway.IsHealthy && _supervisor.Current.Agent.IsHealthy
                    ? "Gateway is healthy and the Windows Agent is connected."
                    : "Open the logs to inspect the failed service.",
                _supervisor.Current.Gateway.IsHealthy && _supervisor.Current.Agent.IsHealthy
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning,
                autoClose: true);
        }
        catch (Exception ex)
        {
            await DesktopLog.WriteAsync("Service restart from Overview failed.", ex);
            ShowOverviewFeedback(
                "Service restart failed",
                ex.Message,
                InfoBarSeverity.Error,
                autoClose: false);
        }
        finally
        {
            SetServiceButtonsEnabled(true);
        }
    }

    private async void RefreshServices_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetServiceButtonsEnabled(false);
            await _supervisor.RefreshAsync();
            ApplySupervisorSnapshot(_supervisor.Current);
        }
        catch (Exception ex)
        {
            await DesktopLog.WriteAsync("Manual service refresh failed.", ex);
            ShowOverviewFeedback(
                "Refresh failed",
                ex.Message,
                InfoBarSeverity.Error,
                autoClose: false);
        }
        finally
        {
            SetServiceButtonsEnabled(true);
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(isManual: true);

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) =>
        await RefreshDeviceSelectionAsync(showErrors: true);

    private async void DefaultDeviceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_ignoreDeviceSelection || DefaultDeviceComboBox.SelectedItem is not DeviceChoice choice)
            return;

        try
        {
            SetServiceButtonsEnabled(false);
            await _deviceSelectionService.SetDefaultDeviceAsync(_supervisor.Current.GatewayUrl, choice.DeviceId);
            await RefreshDeviceSelectionAsync(showErrors: false);
            ShowOverviewFeedback(
                "Default device selected",
                $"AgentBridge will use {choice.Label} when tools do not specify a device.",
                InfoBarSeverity.Success,
                autoClose: true);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException
                                      or TaskCanceledException
                                      or InvalidOperationException)
        {
            await DesktopLog.WriteAsync("Default device selection failed.", ex);
            ShowOverviewFeedback(
                "Could not select device",
                ex.Message,
                InfoBarSeverity.Error,
                autoClose: false);
        }
        finally
        {
            SetServiceButtonsEnabled(true);
        }
    }

    private async Task RefreshDeviceSelectionAsync(bool showErrors)
    {
        if (_isRefreshingDevices)
            return;

        _isRefreshingDevices = true;
        RefreshDevicesButton.IsEnabled = false;

        try
        {
            var response = await _deviceSelectionService.GetDevicesAsync(_supervisor.Current.GatewayUrl);
            var currentDeviceId = _supervisor.Current.DeviceId;
            var preferredDeviceId = response.PreferredDeviceId;

            _ignoreDeviceSelection = true;
            _deviceChoices.Clear();
            foreach (var device in response.Devices)
            {
                var isThisComputer = string.Equals(device.DeviceId, currentDeviceId, StringComparison.OrdinalIgnoreCase);
                var label = isThisComputer
                    ? "This computer"
                    : string.IsNullOrWhiteSpace(device.Label) ? device.DeviceId : device.Label;
                if (!isThisComputer && !string.IsNullOrWhiteSpace(device.DisplayName) && !string.Equals(label, device.DisplayName, StringComparison.Ordinal))
                    label = device.DisplayName;

                _deviceChoices.Add(new DeviceChoice
                {
                    DeviceId = device.DeviceId,
                    Label = label,
                    Online = device.Online,
                    Preferred = device.Preferred
                });
            }

            DefaultDeviceComboBox.SelectedValue = !string.IsNullOrWhiteSpace(preferredDeviceId)
                ? preferredDeviceId
                : _deviceChoices.Count == 1 ? _deviceChoices[0].DeviceId : null;
            _ignoreDeviceSelection = false;

            if (_deviceChoices.Count > 1 && string.IsNullOrWhiteSpace(preferredDeviceId) && showErrors)
            {
                ShowOverviewFeedback(
                    "Choose a default device",
                    "Multiple desktop agents are online. Pick the one AgentBridge should use by default.",
                    InfoBarSeverity.Informational,
                    autoClose: false);
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException
                                      or TaskCanceledException
                                      or System.Text.Json.JsonException
                                      or InvalidOperationException)
        {
            await DesktopLog.WriteAsync("Device list refresh failed.", ex);
            if (showErrors)
            {
                ShowOverviewFeedback(
                    "Could not load devices",
                    ex.Message,
                    InfoBarSeverity.Error,
                    autoClose: false);
            }
        }
        finally
        {
            _ignoreDeviceSelection = false;
            _isRefreshingDevices = false;
            RefreshDevicesButton.IsEnabled = true;
        }
    }

    private async Task CheckForUpdatesAsync(bool isManual)
    {
        if (_isCheckingForUpdates)
            return;

        _isCheckingForUpdates = true;
        CheckUpdatesButton.IsEnabled = false;

        try
        {
            if (isManual)
            {
                ShowOverviewFeedback(
                    "Checking for updates",
                    "Looking for a newer AgentBridge release.",
                    InfoBarSeverity.Informational,
                    autoClose: false);
            }

            var result = await _updateService.CheckAsync(force: isManual);
            if (result.IsSkipped)
                return;

            if (!result.IsUpdateAvailable || result.Manifest is null)
            {
                if (isManual)
                {
                    ShowOverviewFeedback(
                        "No update available",
                        result.Message,
                        InfoBarSeverity.Success,
                        autoClose: true);
                }

                return;
            }

            var latestVersion = result.LatestVersion?.ToString() ?? result.Manifest.Version;
            var answer = System.Windows.MessageBox.Show(
                this,
                $"AgentBridge {latestVersion} is available.\n\nDownload and start the installer now?",
                "Update available",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information);

            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                ShowOverviewFeedback(
                    "Update postponed",
                    $"AgentBridge {latestVersion} is available when you are ready.",
                    InfoBarSeverity.Informational,
                    autoClose: true);
                return;
            }

            ShowOverviewFeedback(
                "Downloading update",
                $"Downloading AgentBridge {latestVersion} and verifying SHA-256.",
                InfoBarSeverity.Informational,
                autoClose: false);

            var downloaded = await _updateService.DownloadPackageAsync(result.Manifest);
            var installAnswer = System.Windows.MessageBox.Show(
                this,
                $"AgentBridge {downloaded.Version} was downloaded and verified.\n\nStart the installer now?",
                "Install update",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information);

            if (installAnswer != System.Windows.MessageBoxResult.Yes)
            {
                ShowOverviewFeedback(
                    "Update downloaded",
                    downloaded.FilePath,
                    InfoBarSeverity.Success,
                    autoClose: false);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = downloaded.FilePath,
                UseShellExecute = true,
                Arguments = "/SP- /CLOSEAPPLICATIONS"
            });

            ShowOverviewFeedback(
                "Installer started",
                "Follow the installer to finish updating AgentBridge.",
                InfoBarSeverity.Success,
                autoClose: false);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException
                                      or InvalidOperationException
                                      or System.Text.Json.JsonException)
        {
            await DesktopLog.WriteAsync("Update check failed.", ex);
            if (isManual)
            {
                ShowOverviewFeedback(
                    "Update check failed",
                    ex.Message,
                    InfoBarSeverity.Error,
                    autoClose: false);
            }
        }
        finally
        {
            _isCheckingForUpdates = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_supervisor.Current.LogsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _supervisor.Current.LogsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowOverviewFeedback(
                "Could not open logs",
                ex.Message,
                InfoBarSeverity.Error,
                autoClose: false);
        }
    }

    private async void AddWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WorkspaceDialog(
            existing: null,
            reservedAliases: _workspaces.Select(workspace => workspace.Alias).ToArray())
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || dialog.Result is null)
            return;

        _workspaces.Add(dialog.Result);
        await SaveAndRefreshAsync("Workspace added");
    }

    private async void EditWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorkspaceConfigurationEntry existing })
            return;

        var dialog = new WorkspaceDialog(
            existing,
            _workspaces
                .Where(workspace => !ReferenceEquals(workspace, existing))
                .Select(workspace => workspace.Alias)
                .ToArray())
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || dialog.Result is null)
            return;

        var index = _workspaces.IndexOf(existing);
        if (index >= 0)
            _workspaces[index] = dialog.Result;

        await SaveAndRefreshAsync("Workspace updated");
    }

    private async void RemoveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorkspaceConfigurationEntry workspace })
            return;

        var answer = System.Windows.MessageBox.Show(
            this,
            $"Remove workspace '{workspace.Alias}'?\n\nThe folder and its files will not be deleted.",
            "Remove workspace",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        _workspaces.Remove(workspace);
        await SaveAndRefreshAsync("Workspace removed");
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private async Task ReloadAsync()
    {
        try
        {
            var workspaces = await _store.LoadAsync();
            _workspaces.Clear();
            foreach (var workspace in workspaces)
                _workspaces.Add(workspace);

            UpdateEmptyState();
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowWorkspaceFeedback(
                "Configuration could not be loaded",
                ex.Message,
                InfoBarSeverity.Error,
                autoClose: false);
        }
    }

    private async Task SaveAndRefreshAsync(string title)
    {
        try
        {
            SetBusy(true);
            await _store.SaveAsync(_workspaces);
            await ReloadAsync();
            var restarted = await _supervisor.RestartAgentAsync();
            ShowWorkspaceFeedback(
                title,
                restarted
                    ? "Saved. Windows Agent restarted with the new access policy."
                    : "Saved. The connected Agent is managed externally and must be restarted manually.",
                restarted ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                autoClose: true);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or System.Text.Json.JsonException)
        {
            await ReloadAsync();
            ShowWorkspaceFeedback(
                "Configuration was not saved",
                ex.Message,
                InfoBarSeverity.Error,
                autoClose: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        IsEnabled = !isBusy;
        Cursor = isBusy ? Cursors.Wait : null;
    }

    private void SetServiceButtonsEnabled(bool enabled)
    {
        RestartServicesButton.IsEnabled = enabled;
        RefreshServicesButton.IsEnabled = enabled;
        OpenLogsButton.IsEnabled = enabled;
        CheckUpdatesButton.IsEnabled = enabled && !_isCheckingForUpdates;
        RefreshDevicesButton.IsEnabled = enabled && !_isRefreshingDevices;
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = _workspaces.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        WorkspaceList.Visibility = _workspaces.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnSupervisorSnapshotChanged(SupervisorSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ApplySupervisorSnapshot(snapshot));
            return;
        }

        ApplySupervisorSnapshot(snapshot);
    }

    private void ApplySupervisorSnapshot(SupervisorSnapshot snapshot)
    {
        GatewayStatusText.Text = snapshot.Gateway.Summary;
        GatewayDetailText.Text = snapshot.Gateway.Detail;
        GatewayMetaText.Text = BuildProcessMeta(snapshot.Gateway);
        AgentStatusText.Text = snapshot.Agent.Summary;
        AgentDetailText.Text = snapshot.Agent.Detail;
        AgentMetaText.Text = BuildProcessMeta(snapshot.Agent);

        var gatewayBrush = GetStatusBrush(snapshot.Gateway.State);
        var agentBrush = GetStatusBrush(snapshot.Agent.State);
        GatewayStatusDot.Fill = gatewayBrush;
        GatewayStatusText.Foreground = gatewayBrush;
        AgentStatusDot.Fill = agentBrush;
        AgentStatusText.Foreground = agentBrush;

        DeviceIdText.Text = string.IsNullOrWhiteSpace(snapshot.DeviceId)
            ? "This computer: preparing…"
            : $"This computer: {snapshot.DeviceId}";
        GatewayUrlText.Text = snapshot.GatewayUrl;
        LogsDirectoryText.Text = snapshot.LogsDirectory;
        LastCheckedText.Text = snapshot.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        if (snapshot.Gateway.State == ManagedServiceState.External
            || snapshot.Agent.State == ManagedServiceState.External)
        {
            OverallStatusText.Text = "External services detected";
            OverallStatusDot.Fill = GetResourceBrush("AgentBridgeWarningBrush");
        }
        else if (snapshot.Gateway.IsHealthy && snapshot.Agent.IsHealthy)
        {
            OverallStatusText.Text = "All systems operational";
            OverallStatusDot.Fill = GetResourceBrush("AgentBridgeSuccessBrush");
        }
        else if (snapshot.Gateway.State == ManagedServiceState.Error
                 || snapshot.Agent.State == ManagedServiceState.Error)
        {
            OverallStatusText.Text = "Attention needed";
            OverallStatusDot.Fill = GetResourceBrush("AgentBridgeDangerBrush");
        }
        else
        {
            OverallStatusText.Text = "Starting services";
            OverallStatusDot.Fill = GetResourceBrush("AgentBridgeWarningBrush");
        }
    }

    private static string BuildProcessMeta(ManagedServiceStatus status)
    {
        if (status.ProcessId is int processId)
        {
            return status.IsManaged
                ? $"PID {processId} • Managed by AgentBridge"
                : $"PID {processId}";
        }

        return status.State == ManagedServiceState.External
            ? "External process"
            : "No active process";
    }

    private Brush GetStatusBrush(ManagedServiceState state) => state switch
    {
        ManagedServiceState.Running or ManagedServiceState.External
            => GetResourceBrush("AgentBridgeSuccessBrush"),
        ManagedServiceState.Error
            => GetResourceBrush("AgentBridgeDangerBrush"),
        ManagedServiceState.Starting
            => GetResourceBrush("AgentBridgeWarningBrush"),
        _ => GetResourceBrush("AgentBridgeSubtleBrush")
    };

    private Brush GetResourceBrush(string key) =>
        FindResource(key) as Brush ?? Brushes.Gray;

    private void ShowWorkspaceFeedback(
        string title,
        string message,
        InfoBarSeverity severity,
        bool autoClose)
    {
        _feedbackTimer.Stop();
        FeedbackInfoBar.Title = title;
        FeedbackInfoBar.Message = message;
        FeedbackInfoBar.Severity = severity;
        FeedbackInfoBar.IsOpen = true;

        if (autoClose)
            _feedbackTimer.Start();
    }

    private void ShowOverviewFeedback(
        string title,
        string message,
        InfoBarSeverity severity,
        bool autoClose)
    {
        _feedbackTimer.Stop();
        OverviewInfoBar.Title = title;
        OverviewInfoBar.Message = message;
        OverviewInfoBar.Severity = severity;
        OverviewInfoBar.IsOpen = true;

        if (autoClose)
            _feedbackTimer.Start();
    }

    private void FeedbackTimer_Tick(object? sender, EventArgs e)
    {
        _feedbackTimer.Stop();
        FeedbackInfoBar.IsOpen = false;
        OverviewInfoBar.IsOpen = false;
    }
}
