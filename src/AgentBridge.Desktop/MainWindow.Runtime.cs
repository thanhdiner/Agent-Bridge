using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AgentBridge.Desktop.Services;
using UiButton = Wpf.Ui.Controls.Button;
using UiControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using UiInfoBar = Wpf.Ui.Controls.InfoBar;
using UiInfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace AgentBridge.Desktop;

public partial class MainWindow
{
    private readonly List<RuntimeProcessRow> _runtimeProcessRows = [];
    private UiButton? _runtimeNavButton;
    private Grid? _runtimePage;
    private UiInfoBar? _runtimeInfoBar;
    private TextBlock? _runtimeStatusText;
    private StackPanel? _runtimeRowsPanel;
    private UiButton? _runtimeRefreshButton;
    private UiButton? _runtimeKillStaleTunnelsButton;
    private UiButton? _runtimeRestartServicesButton;
    private bool _isRefreshingRuntimeProcesses;
    private int _runtimeRefreshVersion;
    private DateTime _lastRuntimeAutoRefreshUtc = DateTime.MinValue;

    private void InitializeRuntimeUi()
    {
        if (Content is not Grid root)
            return;

        var shellGrid = root.Children
            .OfType<Grid>()
            .FirstOrDefault(child => Grid.GetRow(child) == 1 && child.ColumnDefinitions.Count >= 2);
        if (shellGrid is null)
            return;

        var sidebar = shellGrid.Children
            .OfType<Border>()
            .FirstOrDefault(child => Grid.GetColumn(child) == 0);
        var sidebarGrid = sidebar?.Child as Grid;
        var navPanel = sidebarGrid?.Children
            .OfType<StackPanel>()
            .FirstOrDefault(child => Grid.GetRow(child) == 2);
        if (navPanel is null)
            return;

        _runtimeNavButton = new UiButton
        {
            Content = "Runtime",
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.DesktopPulse24 },
            Appearance = UiControlAppearance.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0)
        };
        AutomationProperties.SetAutomationId(_runtimeNavButton, "RuntimeNavButton");
        _runtimeNavButton.Click += RuntimeNav_Click;
        ApplyNavButtonStyle(_runtimeNavButton, false);
        navPanel.Children.Add(_runtimeNavButton);

        _runtimePage = CreateRuntimePage();
        Grid.SetColumn(_runtimePage, 1);
        shellGrid.Children.Add(_runtimePage);
    }

    private Grid CreateRuntimePage()
    {
        var page = new Grid
        {
            Margin = new Thickness(24, 22, 24, 18),
            Visibility = Visibility.Collapsed
        };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titlePanel = new StackPanel();
        titlePanel.Children.Add(new TextBlock
        {
            Text = "Runtime",
            Foreground = GetResourceBrush("AgentBridgeTextBrush"),
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 27,
            FontWeight = FontWeights.SemiBold
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = "Manage Desktop, Gateway, Agent, Tunnel, and external MCP processes.",
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = GetResourceBrush("AgentBridgeMutedBrush"),
            FontSize = 13
        });
        header.Children.Add(titlePanel);

        _runtimeStatusText = new TextBlock
        {
            Text = "Processes: 0",
            Foreground = GetResourceBrush("AgentBridgeTextBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusBorder = new Border
        {
            Background = GetResourceBrush("AgentBridgeSurfaceBrush"),
            BorderBrush = GetResourceBrush("AgentBridgeBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(11, 7, 11, 7),
            Child = _runtimeStatusText
        };
        Grid.SetColumn(statusBorder, 1);
        header.Children.Add(statusBorder);
        page.Children.Add(header);

        _runtimeInfoBar = new UiInfoBar
        {
            IsOpen = false,
            IsClosable = true,
            Severity = UiInfoBarSeverity.Informational
        };
        Grid.SetRow(_runtimeInfoBar, 2);
        page.Children.Add(_runtimeInfoBar);

        var panelBorder = new Border
        {
            Background = GetResourceBrush("AgentBridgeSurfaceBrush"),
            BorderBrush = GetResourceBrush("AgentBridgeBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(17)
        };
        Grid.SetRow(panelBorder, 4);

        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        _runtimeRefreshButton = CreateRuntimeButton("Refresh processes", RuntimeRefresh_Click, UiControlAppearance.Transparent);
        _runtimeKillStaleTunnelsButton = CreateRuntimeButton("Kill stale tunnels", RuntimeKillStaleTunnels_Click, UiControlAppearance.Secondary);
        _runtimeRestartServicesButton = CreateRuntimeButton("Restart services", RuntimeRestartServices_Click, UiControlAppearance.Primary);
        actions.Children.Add(_runtimeRefreshButton);
        actions.Children.Add(_runtimeKillStaleTunnelsButton);
        actions.Children.Add(_runtimeRestartServicesButton);
        panel.Children.Add(actions);

        _runtimeRowsPanel = new StackPanel();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _runtimeRowsPanel
        };
        Grid.SetRow(scroll, 2);
        panel.Children.Add(scroll);

        panelBorder.Child = panel;
        page.Children.Add(panelBorder);

        var footer = new TextBlock
        {
            Text = "Killing Gateway or Agent lets the supervisor restart them. Stale cloudflared processes are old tunnels from previous runs.",
            Foreground = GetResourceBrush("AgentBridgeSubtleBrush"),
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(footer, 6);
        page.Children.Add(footer);

        return page;
    }

    private UiButton CreateRuntimeButton(string content, RoutedEventHandler handler, UiControlAppearance appearance)
    {
        var button = new UiButton
        {
            Content = content,
            Appearance = appearance,
            Margin = new Thickness(0, 0, 10, 8)
        };
        button.Click += handler;
        return button;
    }

    private void RuntimeNav_Click(object sender, RoutedEventArgs e)
    {
        ShowRuntime();
        BeginRuntimeRefresh(showLoading: true);
    }

    private void ShowRuntime()
    {
        OverviewPage.Visibility = Visibility.Collapsed;
        WorkspacePage.Visibility = Visibility.Collapsed;
        if (_toolPage is not null)
            _toolPage.Visibility = Visibility.Collapsed;
        if (_runtimePage is not null)
            _runtimePage.Visibility = Visibility.Visible;

        ApplyNavButtonStyle(OverviewNavButton, false);
        ApplyNavButtonStyle(WorkspacesNavButton, false);
        ApplyNavButtonStyle(_toolsNavButton, false);
        ApplyNavButtonStyle(_runtimeNavButton, true);
    }

    private void RuntimeRefresh_Click(object sender, RoutedEventArgs e) => BeginRuntimeRefresh(showLoading: true);

    private async void RuntimeRestartServices_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetRuntimeButtonsEnabled(false);
            ShowRuntimeFeedback("Restarting services", "Gateway, Agent, and Tunnel are being restarted.", UiInfoBarSeverity.Informational, autoClose: false);
            await _supervisor.RestartAsync();
            ApplySupervisorSnapshot(_supervisor.Current);
            BeginRuntimeRefresh(showLoading: false);
            ShowRuntimeFeedback("Services restarted", "Runtime process list refreshed.", UiInfoBarSeverity.Success, autoClose: true);
        }
        catch (Exception ex)
        {
            await DesktopLog.WriteAsync("Runtime service restart failed.", ex);
            ShowRuntimeFeedback("Restart failed", ex.Message, UiInfoBarSeverity.Error, autoClose: false);
        }
        finally
        {
            SetRuntimeButtonsEnabled(true);
        }
    }

    private async void RuntimeKillStaleTunnels_Click(object sender, RoutedEventArgs e)
    {
        var staleTunnels = CollectRuntimeProcesses()
            .Where(row => row.IsStaleTunnel)
            .ToArray();
        if (staleTunnels.Length == 0)
        {
            ShowRuntimeFeedback("No stale tunnels", "Only the current managed tunnel is running.", UiInfoBarSeverity.Success, autoClose: true);
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            this,
            $"Kill {staleTunnels.Length} stale cloudflared process(es)?",
            "Kill stale tunnels",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        var killed = 0;
        foreach (var row in staleTunnels)
        {
            if (TryKillProcess(row.ProcessId, out _))
                killed++;
        }

        await Task.Delay(400);
        BeginRuntimeRefresh(showLoading: false);
        ShowRuntimeFeedback("Stale tunnels cleaned", $"Killed {killed} stale cloudflared process(es).", UiInfoBarSeverity.Success, autoClose: true);
    }

    private async void RuntimeKillProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RuntimeProcessRow row })
            return;

        var answer = System.Windows.MessageBox.Show(
            this,
            $"Kill {row.Role} PID {row.ProcessId}?\n\n{row.Path}",
            "Kill process",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        if (!TryKillProcess(row.ProcessId, out var errorMessage))
        {
            ShowRuntimeFeedback("Kill failed", errorMessage ?? "Could not kill process.", UiInfoBarSeverity.Error, autoClose: false);
            return;
        }

        await Task.Delay(400);
        BeginRuntimeRefresh(showLoading: false);
        ShowRuntimeFeedback("Process killed", $"Killed PID {row.ProcessId}.", UiInfoBarSeverity.Success, autoClose: true);
    }

    private void BeginRuntimeRefresh(bool showLoading)
    {
        _ = RefreshRuntimeProcessesAsync(showLoading);
    }

    private async Task RefreshRuntimeProcessesAsync(bool showLoading)
    {
        if (_runtimeRowsPanel is null)
            return;

        if (_isRefreshingRuntimeProcesses)
        {
            if (showLoading)
                ShowRuntimeLoadingState();
            return;
        }

        var refreshVersion = ++_runtimeRefreshVersion;
        try
        {
            _isRefreshingRuntimeProcesses = true;
            if (showLoading)
            {
                SetRuntimeButtonsEnabled(false);
                ShowRuntimeLoadingState();
            }

            var rows = await Task.Run(CollectRuntimeProcesses);
            if (refreshVersion != _runtimeRefreshVersion)
                return;

            _runtimeProcessRows.Clear();
            _runtimeProcessRows.AddRange(rows);
            RenderRuntimeProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowRuntimeFeedback("Runtime refresh failed", ex.Message, UiInfoBarSeverity.Error, autoClose: false);
        }
        finally
        {
            _isRefreshingRuntimeProcesses = false;
            if (showLoading && refreshVersion == _runtimeRefreshVersion)
                SetRuntimeButtonsEnabled(true);
        }
    }

    private void ShowRuntimeLoadingState()
    {
        if (_runtimeRowsPanel is null)
            return;

        _runtimeRowsPanel.Children.Clear();
        _runtimeRowsPanel.Children.Add(new Border
        {
            BorderBrush = GetResourceBrush("AgentBridgeBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14),
            Background = GetResourceBrush("AgentBridgeSidebarBrush"),
            Child = new TextBlock
            {
                Text = "Loading runtime processes...",
                Foreground = GetResourceBrush("AgentBridgeMutedBrush"),
                FontSize = 12.5
            }
        });

        if (_runtimeStatusText is not null)
            _runtimeStatusText.Text = "Processes: loading...";
    }

    private IReadOnlyList<RuntimeProcessRow> CollectRuntimeProcesses()
    {
        var snapshot = _supervisor.Current;
        var gatewayPid = snapshot.Gateway.ProcessId;
        var agentPid = snapshot.Agent.ProcessId;
        var tunnelPid = snapshot.Tunnel.ProcessId;
        var currentDesktopPid = Environment.ProcessId;
        var processNames = new[]
        {
            "AgentBridge.Desktop",
            "LocalMcp.Gateway",
            "LocalMcp.Agent.Windows",
            "dotnet",
            "cloudflared",
            "node",
            "npx"
        };

        var rows = processNames
            .SelectMany(GetProcessesByNameSafe)
            .GroupBy(process => process.Id)
            .Select(group => CreateRuntimeProcessRow(group.First(), currentDesktopPid, gatewayPid, agentPid, tunnelPid))
            .Where(row => row is not null)
            .Select(row => row!)
            .ToArray();

        return OrderRuntimeRowsAsTree(rows);
    }

    private static IReadOnlyList<RuntimeProcessRow> OrderRuntimeRowsAsTree(IReadOnlyList<RuntimeProcessRow> rows)
    {
        var rowsById = rows.ToDictionary(row => row.ProcessId);
        var childrenByParent = rows
            .Where(row => row.ParentProcessId is int parentId && rowsById.ContainsKey(parentId))
            .GroupBy(row => row.ParentProcessId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(RuntimeRowSortKey).ThenBy(row => row.ProcessId).ToArray());

        var roots = rows
            .Where(row => row.ParentProcessId is not int parentId || !rowsById.ContainsKey(parentId))
            .OrderBy(RuntimeRowSortKey)
            .ThenBy(row => row.ProcessId)
            .ToArray();

        var ordered = new List<RuntimeProcessRow>(rows.Count);
        var visited = new HashSet<int>();
        foreach (var root in roots)
            AddRuntimeRowWithChildren(root, depth: 0);

        foreach (var row in rows.OrderBy(RuntimeRowSortKey).ThenBy(row => row.ProcessId))
        {
            if (!visited.Contains(row.ProcessId))
                AddRuntimeRowWithChildren(row, depth: 0);
        }

        return ordered;

        void AddRuntimeRowWithChildren(RuntimeProcessRow row, int depth)
        {
            if (!visited.Add(row.ProcessId))
                return;

            ordered.Add(row with { Depth = depth });
            if (!childrenByParent.TryGetValue(row.ProcessId, out var children))
                return;

            foreach (var child in children)
                AddRuntimeRowWithChildren(child, depth + 1);
        }
    }

    private static (int SortOrder, DateTime StartTime, int ProcessId) RuntimeRowSortKey(RuntimeProcessRow row) =>
        (row.SortOrder, row.StartTimeUtc ?? DateTime.MinValue, row.ProcessId);

    private RuntimeProcessRow? CreateRuntimeProcessRow(
        Process process,
        int currentDesktopPid,
        int? gatewayPid,
        int? agentPid,
        int? tunnelPid)
    {
        var processName = SafeProcessName(process);
        if (string.IsNullOrWhiteSpace(processName))
            return null;

        var processId = process.Id;
        var path = SafeProcessPath(process);
        var startTime = SafeStartTime(process);
        var role = ClassifyRuntimeRole(processName, processId, path, currentDesktopPid, gatewayPid, agentPid, tunnelPid);
        var isCurrentDesktop = processId == currentDesktopPid;
        var isCurrentManaged = processId == gatewayPid || processId == agentPid || processId == tunnelPid;
        var isStaleTunnel = string.Equals(processName, "cloudflared", StringComparison.OrdinalIgnoreCase)
                            && tunnelPid is int currentTunnelPid
                            && processId != currentTunnelPid;
        var state = isCurrentDesktop
            ? "Current desktop"
            : isCurrentManaged
                ? "Current managed"
                : isStaleTunnel
                    ? "Stale tunnel"
                    : "Untracked";
        var parentProcessId = ResolveLogicalParentProcessId(
            role,
            processId,
            currentDesktopPid,
            gatewayPid,
            agentPid,
            tunnelPid,
            isCurrentDesktop,
            isCurrentManaged,
            isStaleTunnel);

        return new RuntimeProcessRow(
            Role: role,
            ProcessName: processName,
            ProcessId: processId,
            StartTimeText: startTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown",
            StartTimeUtc: startTime,
            Path: path,
            State: state,
            ParentProcessId: parentProcessId,
            Depth: 0,
            CanKill: !isCurrentDesktop,
            IsStaleTunnel: isStaleTunnel,
            SortOrder: GetRuntimeSortOrder(role, isCurrentManaged, isStaleTunnel));
    }

    private static int? ResolveLogicalParentProcessId(
        string role,
        int processId,
        int currentDesktopPid,
        int? gatewayPid,
        int? agentPid,
        int? tunnelPid,
        bool isCurrentDesktop,
        bool isCurrentManaged,
        bool isStaleTunnel)
    {
        if (isCurrentDesktop || isStaleTunnel)
            return null;

        if (isCurrentManaged)
            return currentDesktopPid;

        if (role == "External MCP" && gatewayPid is int currentGatewayPid && processId != currentGatewayPid)
            return currentGatewayPid;

        if (role == "Gateway" || role == "Agent" || role == "Tunnel")
            return currentDesktopPid;

        return null;
    }

    private string ClassifyRuntimeRole(
        string processName,
        int processId,
        string path,
        int currentDesktopPid,
        int? gatewayPid,
        int? agentPid,
        int? tunnelPid)
    {
        if (processId == currentDesktopPid || string.Equals(processName, "AgentBridge.Desktop", StringComparison.OrdinalIgnoreCase))
            return "Desktop";
        if (processId == gatewayPid || path.Contains("LocalMcp.Gateway", StringComparison.OrdinalIgnoreCase))
            return "Gateway";
        if (processId == agentPid || path.Contains("LocalMcp.Agent.Windows", StringComparison.OrdinalIgnoreCase))
            return "Agent";
        if (processId == tunnelPid || string.Equals(processName, "cloudflared", StringComparison.OrdinalIgnoreCase))
            return "Tunnel";
        if (string.Equals(processName, "node", StringComparison.OrdinalIgnoreCase) || string.Equals(processName, "npx", StringComparison.OrdinalIgnoreCase))
            return "External MCP";
        return "dotnet";
    }

    private static int GetRuntimeSortOrder(string role, bool isCurrentManaged, bool isStaleTunnel)
    {
        if (role == "Desktop")
            return 0;
        if (isCurrentManaged)
            return 1;
        if (isStaleTunnel)
            return 2;
        return role switch
        {
            "Gateway" => 3,
            "Agent" => 4,
            "Tunnel" => 5,
            "External MCP" => 6,
            _ => 7
        };
    }

    private void RenderRuntimeProcesses()
    {
        if (_runtimeRowsPanel is null)
            return;

        _runtimeRowsPanel.Children.Clear();
        foreach (var row in _runtimeProcessRows)
            _runtimeRowsPanel.Children.Add(CreateRuntimeProcessRowElement(row));

        var staleTunnelCount = _runtimeProcessRows.Count(row => row.IsStaleTunnel);
        var nodeCount = _runtimeProcessRows.Count(row => row.Role == "External MCP");
        if (_runtimeStatusText is not null)
        {
            _runtimeStatusText.Text = $"Processes: {_runtimeProcessRows.Count}   Stale tunnels: {staleTunnelCount}   External MCP node/npx: {nodeCount}";
        }

        if (_runtimeKillStaleTunnelsButton is not null)
            _runtimeKillStaleTunnelsButton.IsEnabled = staleTunnelCount > 0;
    }

    private Border CreateRuntimeProcessRowElement(RuntimeProcessRow row)
    {
        var border = new Border
        {
            Background = row.IsStaleTunnel ? GetResourceBrush("AgentBridgeAccentSoftBrush") : GetResourceBrush("AgentBridgeSidebarBrush"),
            BorderBrush = row.IsStaleTunnel ? GetResourceBrush("AgentBridgeWarningBrush") : GetResourceBrush("AgentBridgeBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Margin = new Thickness(Math.Min(row.Depth, 6) * 18, 0, 0, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var roleTitle = row.Depth == 0 ? row.Role : $"↳ {row.Role}";
        var stateDetail = row.ParentProcessId is int parentProcessId
            ? $"{row.State} • child of PID {parentProcessId}"
            : row.State;
        grid.Children.Add(CreateRuntimeTextStack(roleTitle, stateDetail, row.IsStaleTunnel));
        var pidStack = CreateRuntimeTextStack($"PID {row.ProcessId}", row.ProcessName, false);
        Grid.SetColumn(pidStack, 1);
        grid.Children.Add(pidStack);
        var startStack = CreateRuntimeTextStack("Started", row.StartTimeText, false);
        Grid.SetColumn(startStack, 2);
        grid.Children.Add(startStack);

        var pathText = new TextBlock
        {
            Text = row.Path,
            Foreground = GetResourceBrush("AgentBridgeMutedBrush"),
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = row.Path,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(pathText, 3);
        grid.Children.Add(pathText);

        var killButton = new UiButton
        {
            Content = row.Role == "Tunnel" && row.IsStaleTunnel ? "Kill stale" : "Kill",
            Appearance = UiControlAppearance.Transparent,
            Foreground = GetResourceBrush("AgentBridgeDangerBrush"),
            IsEnabled = row.CanKill,
            Tag = row,
            Margin = new Thickness(10, 0, 0, 0)
        };
        killButton.Click += RuntimeKillProcess_Click;
        Grid.SetColumn(killButton, 4);
        grid.Children.Add(killButton);

        border.Child = grid;
        return border;
    }

    private StackPanel CreateRuntimeTextStack(string title, string detail, bool warn)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = warn ? GetResourceBrush("AgentBridgeWarningBrush") : GetResourceBrush("AgentBridgeTextBrush"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = GetResourceBrush("AgentBridgeMutedBrush"),
            FontSize = 10.5,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        return stack;
    }

    private void SetRuntimeButtonsEnabled(bool enabled)
    {
        if (_runtimeRefreshButton is not null)
            _runtimeRefreshButton.IsEnabled = enabled;
        if (_runtimeKillStaleTunnelsButton is not null)
            _runtimeKillStaleTunnelsButton.IsEnabled = enabled && _runtimeProcessRows.Any(row => row.IsStaleTunnel);
        if (_runtimeRestartServicesButton is not null)
            _runtimeRestartServicesButton.IsEnabled = enabled;
    }

    private void UpdateRuntimeProcessPageIfVisible()
    {
        if (_runtimePage?.Visibility != Visibility.Visible)
            return;

        var now = DateTime.UtcNow;
        if (now - _lastRuntimeAutoRefreshUtc < TimeSpan.FromSeconds(2))
            return;

        _lastRuntimeAutoRefreshUtc = now;
        BeginRuntimeRefresh(showLoading: false);
    }

    private void ShowRuntimeFeedback(string title, string message, UiInfoBarSeverity severity, bool autoClose)
    {
        if (_runtimeInfoBar is null)
            return;

        _feedbackTimer.Stop();
        _runtimeInfoBar.Title = title;
        _runtimeInfoBar.Message = message;
        _runtimeInfoBar.Severity = severity;
        _runtimeInfoBar.IsOpen = true;

        if (autoClose)
            _feedbackTimer.Start();
    }

    private static IEnumerable<Process> GetProcessesByNameSafe(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName);
        }
        catch
        {
            return Array.Empty<Process>();
        }
    }

    private static bool TryKillProcess(int processId, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return true;

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string SafeProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static DateTime? SafeStartTime(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static string SafeProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed record RuntimeProcessRow(
        string Role,
        string ProcessName,
        int ProcessId,
        string StartTimeText,
        DateTime? StartTimeUtc,
        string Path,
        string State,
        int? ParentProcessId,
        int Depth,
        bool CanKill,
        bool IsStaleTunnel,
        int SortOrder);
}
