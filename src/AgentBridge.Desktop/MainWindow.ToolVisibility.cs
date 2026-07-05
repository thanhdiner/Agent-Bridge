using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions ToolVisibilityJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _toolVisibilityHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    private readonly Dictionary<string, CheckBox> _toolCheckboxesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _toolSelectionByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComboBox> _toolConnectionsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _toolConnectionStateByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _toolSourceByName = new(StringComparer.OrdinalIgnoreCase);
    private string[] _visibleToolNames = Array.Empty<string>();
    private ToolVisibilityDesktopSnapshot? _lastToolVisibilitySnapshot;
    private UiButton? _toolsNavButton;
    private Grid? _toolPage;
    private UiInfoBar? _toolInfoBar;
    private TextBlock? _toolStatusText;
    private TextBlock? _toolTotalStatusText;
    private TextBlock? _toolConfigPathText;
    private StackPanel? _toolGroupsPanel;
    private ComboBox? _toolSourceFilterBox;
    private ComboBox? _toolShardFilterBox;
    private UiButton? _toolRefreshButton;
    private UiButton? _toolMoveSelectedToAButton;
    private UiButton? _toolMoveSelectedToBButton;
    private UiButton? _toolDisableSelectedButton;
    private UiButton? _toolApplyButton;
    private UiButton? _toolApplyRestartButton;
    private string _toolMode = "all";
    private string _toolSourceFilter = "all";
    private string _toolShardFilter = "all";
    private int _toolConnectionLimit = 150;
    private bool _toolVisibilityLoaded;
    private bool _isRefreshingToolVisibility;
    private bool _ignoreToolCheckboxChanges;

    private void InitializeToolVisibilityUi()
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

        _toolsNavButton = new UiButton
        {
            Content = "Tools",
            Appearance = UiControlAppearance.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0)
        };
        AutomationProperties.SetAutomationId(_toolsNavButton, "ToolsNavButton");
        _toolsNavButton.Click += ToolsNav_Click;
        navPanel.Children.Add(_toolsNavButton);

        _toolPage = CreateToolVisibilityPage();
        Grid.SetColumn(_toolPage, 1);
        shellGrid.Children.Add(_toolPage);
    }

    private Grid CreateToolVisibilityPage()
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
            Text = "Tool Visibility",
            Foreground = GetResourceBrush("AgentBridgeTextBrush"),
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 27,
            FontWeight = FontWeights.SemiBold
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = "Choose exactly which MCP tools ChatGPT can see and call.",
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = GetResourceBrush("AgentBridgeMutedBrush"),
            FontSize = 13
        });
        header.Children.Add(titlePanel);

        var statusStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        _toolStatusText = new TextBlock
        {
            Text = "Connection A: 0 / 150   Connection B: 0 / 150",
            Foreground = GetResourceBrush("AgentBridgeTextBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };
        _toolTotalStatusText = new TextBlock
        {
            Text = "Total: 0 tools",
            Foreground = GetResourceBrush("AgentBridgeMutedBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0)
        };
        statusStack.Children.Add(_toolStatusText);
        statusStack.Children.Add(_toolTotalStatusText);
        var statusBorder = new Border
        {
            Background = GetResourceBrush("AgentBridgeSurfaceBrush"),
            BorderBrush = GetResourceBrush("AgentBridgeBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11, 7, 11, 7),
            Child = statusStack
        };
        Grid.SetColumn(statusBorder, 1);
        header.Children.Add(statusBorder);
        page.Children.Add(header);

        _toolInfoBar = new UiInfoBar
        {
            IsOpen = false,
            IsClosable = true,
            Severity = UiInfoBarSeverity.Informational
        };
        Grid.SetRow(_toolInfoBar, 2);
        page.Children.Add(_toolInfoBar);

        var toolPanelBorder = new Border
        {
            Background = GetResourceBrush("AgentBridgeSurfaceBrush"),
            BorderBrush = GetResourceBrush("AgentBridgeBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(17)
        };
        Grid.SetRow(toolPanelBorder, 4);

        var toolPanel = new Grid();
        toolPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toolPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        toolPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        _toolRefreshButton = CreateToolButton("Refresh", ToolRefresh_Click, UiControlAppearance.Transparent);
        _toolSourceFilterBox = CreateToolFilterBox(new[] { "All", "Local", "External" }, ToolSourceFilter_Changed);
        _toolShardFilterBox = CreateToolFilterBox(new[] { "All", "A", "B", "Unassigned" }, ToolShardFilter_Changed);
        _toolMoveSelectedToAButton = CreateToolButton("Move selected tools to A", ToolMoveSelectedToA_Click, UiControlAppearance.Secondary);
        _toolMoveSelectedToBButton = CreateToolButton("Move selected tools to B", ToolMoveSelectedToB_Click, UiControlAppearance.Secondary);
        _toolDisableSelectedButton = CreateToolButton("Disable selected tools", ToolDisableSelected_Click, UiControlAppearance.Secondary);
        _toolApplyButton = CreateToolButton("Apply", ToolApply_Click, UiControlAppearance.Primary);
        _toolApplyRestartButton = CreateToolButton("Apply & Restart services", ToolApplyRestart_Click, UiControlAppearance.Primary);
        buttons.Children.Add(_toolRefreshButton);
        buttons.Children.Add(CreateToolFilterLabel("Source"));
        buttons.Children.Add(_toolSourceFilterBox);
        buttons.Children.Add(CreateToolFilterLabel("Shard"));
        buttons.Children.Add(_toolShardFilterBox);
        buttons.Children.Add(_toolMoveSelectedToAButton);
        buttons.Children.Add(_toolMoveSelectedToBButton);
        buttons.Children.Add(_toolDisableSelectedButton);
        buttons.Children.Add(_toolApplyButton);
        buttons.Children.Add(_toolApplyRestartButton);
        toolPanel.Children.Add(buttons);

        _toolGroupsPanel = new StackPanel();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _toolGroupsPanel
        };
        Grid.SetRow(scroll, 2);
        toolPanel.Children.Add(scroll);

        toolPanelBorder.Child = toolPanel;
        page.Children.Add(toolPanelBorder);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _toolConfigPathText = new TextBlock
        {
            Foreground = GetResourceBrush("AgentBridgeSubtleBrush"),
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        footer.Children.Add(_toolConfigPathText);

        var notice = new TextBlock
        {
            Text = "After applying, refresh/reconnect MCP tools in ChatGPT.",
            Foreground = GetResourceBrush("AgentBridgeWarningBrush"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(notice, 1);
        footer.Children.Add(notice);
        Grid.SetRow(footer, 6);
        page.Children.Add(footer);

        return page;
    }

    private UiButton CreateToolButton(string content, RoutedEventHandler handler, UiControlAppearance appearance)
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

    private TextBlock CreateToolFilterLabel(string text) => new()
    {
        Text = text,
        Foreground = GetResourceBrush("AgentBridgeMutedBrush"),
        FontSize = 11.5,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 6, 8)
    };

    private ComboBox CreateToolFilterBox(string[] values, SelectionChangedEventHandler handler)
    {
        var combo = new ComboBox
        {
            Width = 118,
            MinWidth = 118,
            Margin = new Thickness(0, 0, 10, 8),
            VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = values,
            SelectedItem = values[0]
        };
        combo.SelectionChanged += handler;
        return combo;
    }

    private void ToolsNav_Click(object sender, RoutedEventArgs e)
    {
        ShowTools();
        if (!_toolVisibilityLoaded)
            ShowToolVisibilityLoadingState();

        _ = RefreshToolVisibilityAsync(showErrors: true);
    }

    private void ShowTools()
    {
        OverviewPage.Visibility = Visibility.Collapsed;
        WorkspacePage.Visibility = Visibility.Collapsed;
        if (_toolPage is not null)
            _toolPage.Visibility = Visibility.Visible;
        if (_runtimePage is not null)
            _runtimePage.Visibility = Visibility.Collapsed;

        OverviewNavButton.Appearance = UiControlAppearance.Transparent;
        WorkspacesNavButton.Appearance = UiControlAppearance.Transparent;
        if (_toolsNavButton is not null)
            _toolsNavButton.Appearance = UiControlAppearance.Primary;
        if (_runtimeNavButton is not null)
            _runtimeNavButton.Appearance = UiControlAppearance.Transparent;
    }

    private async void ToolRefresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshToolVisibilityAsync(showErrors: true);

    private void ToolMoveSelectedToA_Click(object sender, RoutedEventArgs e) =>
        AssignTools(GetBulkToolNames(), "A");

    private void ToolMoveSelectedToB_Click(object sender, RoutedEventArgs e) =>
        AssignTools(GetBulkToolNames(), "B");

    private void ToolDisableSelected_Click(object sender, RoutedEventArgs e) =>
        AssignTools(GetBulkToolNames(), "None");

    private void ToolSourceFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is string selected)
        {
            _toolSourceFilter = NormalizeToolFilter(selected);
            RenderCurrentToolVisibilityPreservingState();
        }
    }

    private void ToolShardFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is string selected)
        {
            _toolShardFilter = NormalizeToolFilter(selected);
            RenderCurrentToolVisibilityPreservingState();
        }
    }

    private void RenderCurrentToolVisibilityPreservingState()
    {
        if (_lastToolVisibilitySnapshot is null || _isRefreshingToolVisibility)
        {
            UpdateVisibleBulkButtonLabels();
            return;
        }

        RenderToolVisibility(_lastToolVisibilitySnapshot, preserveCurrentConnections: true);
    }

    private async void ToolApply_Click(object sender, RoutedEventArgs e) =>
        await ApplyToolVisibilityAsync(restartServices: false);

    private async void ToolApplyRestart_Click(object sender, RoutedEventArgs e) =>
        await ApplyToolVisibilityAsync(restartServices: true);

    private async Task RefreshToolVisibilityAsync(bool showErrors)
    {
        if (_toolGroupsPanel is null)
            return;
        if (_isRefreshingToolVisibility)
            return;

        try
        {
            _isRefreshingToolVisibility = true;
            SetToolVisibilityBusy(true);
            var snapshot = await _toolVisibilityHttpClient.GetFromJsonAsync<ToolVisibilityDesktopSnapshot>(
                BuildToolVisibilityEndpoint(),
                ToolVisibilityJsonOptions);
            if (snapshot is null)
                throw new InvalidOperationException("Gateway returned an empty tool visibility snapshot.");

            RenderToolVisibility(snapshot);
            _toolVisibilityLoaded = true;
            if (snapshot.TotalCount == 0 && showErrors)
            {
                ShowToolVisibilityFeedback(
                    "No tool catalog yet",
                    "Open or reconnect ChatGPT so it calls tools/list once, then press Refresh.",
                    UiInfoBarSeverity.Warning,
                    autoClose: false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            await DesktopLog.WriteAsync("Tool visibility refresh failed.", ex);
            if (showErrors)
            {
                ShowToolVisibilityFeedback(
                    "Could not load tools",
                    ex.Message,
                    UiInfoBarSeverity.Error,
                    autoClose: false);
            }
        }
        finally
        {
            _isRefreshingToolVisibility = false;
            SetToolVisibilityBusy(false);
        }
    }

    private void ShowToolVisibilityLoadingState()
    {
        if (_toolGroupsPanel is null)
            return;

        _toolGroupsPanel.Children.Clear();
        _toolGroupsPanel.Children.Add(new Border
        {
            BorderBrush = GetResourceBrush("AgentBridgeBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Background = GetResourceBrush("AgentBridgeSidebarBrush"),
            Child = new TextBlock
            {
                Text = "Loading tools...",
                Foreground = GetResourceBrush("AgentBridgeMutedBrush"),
                FontSize = 12.5
            }
        });
    }

    private void RenderToolVisibility(ToolVisibilityDesktopSnapshot snapshot, bool preserveCurrentConnections = false)
    {
        if (_toolGroupsPanel is null)
            return;

        _lastToolVisibilitySnapshot = snapshot;
        _toolMode = string.Equals(snapshot.Mode, "custom", StringComparison.OrdinalIgnoreCase)
            ? "custom"
            : "all";
        _toolConnectionLimit = snapshot.MaxEnabledToolsPerConnection > 0
            ? snapshot.MaxEnabledToolsPerConnection
            : 150;
        _toolCheckboxesByName.Clear();
        _toolSelectionByName.Clear();
        _toolConnectionsByName.Clear();
        if (!preserveCurrentConnections)
            _toolConnectionStateByName.Clear();
        _toolSourceByName.Clear();
        _visibleToolNames = Array.Empty<string>();
        _toolGroupsPanel.Children.Clear();
        _ignoreToolCheckboxChanges = true;

        var allTools = snapshot.Groups.SelectMany(group => group.Tools).ToArray();
        foreach (var tool in allTools)
        {
            _toolSourceByName[tool.Name] = NormalizeDesktopSource(tool.Source, tool.Name);
            if (!preserveCurrentConnections || !_toolConnectionStateByName.ContainsKey(tool.Name))
                _toolConnectionStateByName[tool.Name] = GetToolConnectionFromSnapshot(tool);
        }

        if (snapshot.ExternalServers.Length > 0)
            _toolGroupsPanel.Children.Add(CreateExternalServerStatusPanel(snapshot.ExternalServers));

        var visibleNames = new List<string>();
        foreach (var group in snapshot.Groups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase))
        {
            var visibleGroupTools = group.Tools
                .Where(ToolMatchesCurrentFilters)
                .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (visibleGroupTools.Length == 0)
                continue;

            visibleNames.AddRange(visibleGroupTools.Select(tool => tool.Name));
            var isExpanded = false;
            var groupBorder = new Border
            {
                BorderBrush = GetResourceBrush("AgentBridgeBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 10),
                Background = GetResourceBrush("AgentBridgeSidebarBrush")
            };

            var groupStack = new StackPanel();
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftHeader = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            var expandButton = new UiButton
            {
                Content = ">",
                Appearance = UiControlAppearance.Transparent,
                Width = 28,
                MinWidth = 28,
                Height = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 4, 0)
            };
            var groupCheck = new CheckBox
            {
                Content = group.Name,
                Foreground = GetResourceBrush("AgentBridgeTextBrush"),
                FontWeight = FontWeights.SemiBold,
                IsThreeState = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Checked means every visible tool in this group is exposed to ChatGPT. Dash means only some are exposed."
            };
            leftHeader.Children.Add(expandButton);
            leftHeader.Children.Add(groupCheck);
            header.Children.Add(leftHeader);
            var groupActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            var groupNames = visibleGroupTools.Select(tool => tool.Name).ToArray();
            var activeCount = visibleGroupTools.Count(tool => GetCurrentToolConnection(tool.Name) is "A" or "B");
            var groupACount = visibleGroupTools.Count(tool => GetCurrentToolConnection(tool.Name) == "A");
            var groupBCount = visibleGroupTools.Count(tool => GetCurrentToolConnection(tool.Name) == "B");
            groupCheck.IsChecked = activeCount == 0
                ? false
                : activeCount == visibleGroupTools.Length ? true : null;
            groupActions.Children.Add(CreateToolButton("Enable group into A", (_, _) => AssignTools(groupNames, "A"), UiControlAppearance.Secondary));
            groupActions.Children.Add(CreateToolButton("Enable group into B", (_, _) => AssignTools(groupNames, "B"), UiControlAppearance.Secondary));
            groupActions.Children.Add(new TextBlock
            {
                Text = $"A {groupACount} • B {groupBCount} • {activeCount} / {visibleGroupTools.Length}",
                Foreground = GetResourceBrush("AgentBridgeMutedBrush"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            });
            header.Children.Add(groupActions);
            Grid.SetColumn(header.Children[^1], 1);
            groupStack.Children.Add(header);

            var childrenStack = new StackPanel { Margin = new Thickness(22, 8, 0, 0) };
            childrenStack.Visibility = Visibility.Collapsed;
            var childSelections = new List<CheckBox>();

            foreach (var tool in visibleGroupTools)
            {
                var row = CreateToolRow(tool);
                childrenStack.Children.Add(row.Panel);
                childSelections.Add(row.SelectionCheckBox);
                _toolSelectionByName[tool.Name] = row.SelectionCheckBox;
                _toolCheckboxesByName[tool.Name] = row.EnabledCheckBox;
                _toolConnectionsByName[tool.Name] = row.ConnectionBox;
            }

            groupCheck.Checked += (_, _) => AssignTools(groupNames, "A");
            groupCheck.Unchecked += (_, _) => AssignTools(groupNames, "None");
            expandButton.Click += (_, _) =>
            {
                isExpanded = !isExpanded;
                childrenStack.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
                expandButton.Content = isExpanded ? "v" : ">";
            };

            groupStack.Children.Add(childrenStack);
            groupBorder.Child = groupStack;
            _toolGroupsPanel.Children.Add(groupBorder);
        }

        _visibleToolNames = visibleNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _ignoreToolCheckboxChanges = false;
        if (_toolConfigPathText is not null)
            _toolConfigPathText.Text = snapshot.ConfigPath;
        UpdateVisibleBulkButtonLabels();
        UpdateToolStatusText(snapshot.TotalCount);
    }

    private (Grid Panel, CheckBox SelectionCheckBox, CheckBox EnabledCheckBox, ComboBox ConnectionBox) CreateToolRow(ToolVisibilityDesktopTool tool)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 0, 0, 7)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var selectionCheck = new CheckBox
        {
            Content = "Select",
            IsChecked = false,
            Foreground = GetResourceBrush("AgentBridgeMutedBrush"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            ToolTip = "Select this tool for bulk actions"
        };
        selectionCheck.Checked += ToolSelection_Changed;
        selectionCheck.Unchecked += ToolSelection_Changed;
        row.Children.Add(selectionCheck);

        var connection = GetCurrentToolConnection(tool.Name);
        var check = new CheckBox
        {
            Content = tool.Name,
            IsChecked = connection is "A" or "B",
            Foreground = GetResourceBrush("AgentBridgeTextBrush"),
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = tool.Name,
            ToolTip = string.IsNullOrWhiteSpace(tool.Title) ? tool.Name : tool.Title
        };
        check.Checked += ToolCheckbox_Changed;
        check.Unchecked += ToolCheckbox_Changed;
        Grid.SetColumn(check, 1);
        row.Children.Add(check);

        var connectionBox = new ComboBox
        {
            Width = 108,
            MinWidth = 108,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = new[] { "None", "A", "B" },
            SelectedItem = connection,
            Tag = tool.Name,
            ToolTip = connection
        };
        connectionBox.SelectionChanged += ToolConnection_Changed;
        Grid.SetColumn(connectionBox, 2);
        row.Children.Add(connectionBox);

        var labels = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        labels.Children.Add(CreateToolLabel(NormalizeDesktopSource(tool.Source, tool.Name), "AgentBridgeSubtleBrush"));
        labels.Children.Add(CreateToolLabel(tool.Risk, string.Equals(tool.Risk, "dangerous", StringComparison.OrdinalIgnoreCase)
            ? "AgentBridgeDangerBrush"
            : "AgentBridgeSuccessBrush"));
        Grid.SetColumn(labels, 3);
        row.Children.Add(labels);

        return (row, selectionCheck, check, connectionBox);
    }

    private Border CreateExternalServerStatusPanel(IReadOnlyList<ToolVisibilityExternalServer> servers)
    {
        var border = new Border
        {
            BorderBrush = GetResourceBrush("AgentBridgeBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10),
            Background = GetResourceBrush("AgentBridgeSidebarBrush")
        };

        var stack = new StackPanel();
        var details = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(28, 7, 0, 0)
        };
        var okCount = servers.Count(server => string.Equals(server.Status, "ok", StringComparison.OrdinalIgnoreCase));
        var errorCount = servers.Count - okCount;
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var expandButton = new UiButton
        {
            Content = ">",
            Appearance = UiControlAppearance.Transparent,
            Width = 28,
            MinWidth = 28,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0)
        };
        var title = errorCount == 0
            ? $"External MCP servers: {okCount} ok"
            : $"External MCP servers: {okCount} ok, {errorCount} error";
        header.Children.Add(expandButton);
        header.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = GetResourceBrush("AgentBridgeTextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(header);
        var isExpanded = false;
        expandButton.Click += (_, _) =>
        {
            isExpanded = !isExpanded;
            details.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
            expandButton.Content = isExpanded ? "v" : ">";
        };

        foreach (var server in servers.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase))
        {
            var statusBrush = string.Equals(server.Status, "ok", StringComparison.OrdinalIgnoreCase)
                ? "AgentBridgeSuccessBrush"
                : "AgentBridgeDangerBrush";
            details.Children.Add(new TextBlock
            {
                Text = $"{server.Name}: {server.Status}, {server.ToolCount} tools - {server.Message}",
                Foreground = GetResourceBrush(statusBrush),
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }
        stack.Children.Add(details);

        border.Child = stack;
        return border;
    }

    private TextBlock CreateToolLabel(string text, string brushKey) => new()
    {
        Text = text,
        Foreground = GetResourceBrush(brushKey),
        FontSize = 10.5,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private bool ToolMatchesCurrentFilters(ToolVisibilityDesktopTool tool)
    {
        var source = NormalizeDesktopSource(tool.Source, tool.Name);
        if (_toolSourceFilter is not "all" && !string.Equals(source, _toolSourceFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (_toolShardFilter is "all")
            return true;

        var connection = GetCurrentToolConnection(tool.Name);
        var shard = connection switch
        {
            "A" => "a",
            "B" => "b",
            _ => "unassigned"
        };
        return string.Equals(shard, _toolShardFilter, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasActiveToolFilter() =>
        _toolSourceFilter is not "all" || _toolShardFilter is not "all";

    private string[] GetVisibleToolNames() =>
        _visibleToolNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private string[] GetBulkToolNames() =>
        HasActiveToolFilter()
            ? GetVisibleToolNames()
            : GetSelectedToolNames();

    private void UpdateVisibleBulkButtonLabels()
    {
        if (HasActiveToolFilter())
        {
            var visibleCount = GetVisibleToolNames().Length;
            if (_toolMoveSelectedToAButton is not null) _toolMoveSelectedToAButton.Content = $"Enable {visibleCount} visible to A";
            if (_toolMoveSelectedToBButton is not null) _toolMoveSelectedToBButton.Content = $"Enable {visibleCount} visible to B";
            if (_toolDisableSelectedButton is not null) _toolDisableSelectedButton.Content = $"Disable {visibleCount} visible";
            return;
        }

        var selectedCount = GetSelectedToolNames().Length;
        var selectedText = selectedCount == 0 ? "selected tools" : $"{selectedCount} selected";
        if (_toolMoveSelectedToAButton is not null) _toolMoveSelectedToAButton.Content = $"Move {selectedText} to A";
        if (_toolMoveSelectedToBButton is not null) _toolMoveSelectedToBButton.Content = $"Move {selectedText} to B";
        if (_toolDisableSelectedButton is not null) _toolDisableSelectedButton.Content = selectedCount == 0
            ? "Disable selected tools"
            : $"Disable {selectedCount} selected";
    }

    private string GetToolConnectionFromSnapshot(ToolVisibilityDesktopTool tool)
    {
        var assignment = NormalizeDesktopAssignment(tool.Assignment) ?? NormalizeDesktopAssignment(tool.Shard);
        return assignment switch
        {
            "a" => "A",
            "b" => "B",
            _ => NormalizeDesktopConnection(tool.Connection)
        };
    }

    private string GetCurrentToolConnection(string toolName) =>
        _toolConnectionStateByName.TryGetValue(toolName, out var connection)
            ? NormalizeDesktopConnection(connection)
            : "None";

    private static string NormalizeToolFilter(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "local" or "external" or "a" or "b" or "unassigned"
            ? normalized
            : "all";
    }

    private static string NormalizeDesktopSource(string? source, string? toolName)
    {
        if (string.Equals(source, "local", StringComparison.OrdinalIgnoreCase))
            return "local";

        if (string.Equals(source, "external", StringComparison.OrdinalIgnoreCase))
            return "external";

        return !string.IsNullOrWhiteSpace(toolName) && toolName.Contains('.', StringComparison.Ordinal)
            ? "external"
            : "local";
    }

    private static string? NormalizeDesktopAssignment(string? assignment)
    {
        if (string.Equals(assignment, "a", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assignment, "A", StringComparison.OrdinalIgnoreCase))
            return "a";

        if (string.Equals(assignment, "b", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assignment, "B", StringComparison.OrdinalIgnoreCase))
            return "b";

        return null;
    }

    private void ToolCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (_ignoreToolCheckboxChanges)
            return;

        _toolMode = "custom";
        if (sender is CheckBox check && check.Tag is string toolName)
        {
            var desiredConnection = check.IsChecked == true
                ? GetToolConnection(toolName) is "A" or "B" ? GetToolConnection(toolName) : "A"
                : "None";
            AssignTools(new[] { toolName }, desiredConnection);
            return;
        }

        UpdateToolStatusText();
    }

    private void ToolConnection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreToolCheckboxChanges)
            return;

        if (sender is ComboBox combo && combo.Tag is string toolName && combo.SelectedItem is string connection)
            AssignTools(new[] { toolName }, connection);
    }

    private void ToolSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (_ignoreToolCheckboxChanges)
            return;

        UpdateVisibleBulkButtonLabels();
    }

    private void SetGroupSelection(IReadOnlyList<CheckBox> childChecks, bool isChecked)
    {
        if (_ignoreToolCheckboxChanges)
            return;

        _toolMode = "custom";
        _ignoreToolCheckboxChanges = true;
        foreach (var child in childChecks)
            child.IsChecked = isChecked;
        _ignoreToolCheckboxChanges = false;
        UpdateVisibleBulkButtonLabels();
        UpdateToolStatusText();
    }

    private void AssignTools(IReadOnlyList<string> toolNames, string connection)
    {
        var names = toolNames
            .Where(name => _toolConnectionStateByName.ContainsKey(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
            return;

        var normalizedConnection = NormalizeDesktopConnection(connection);
        var previousConnections = names.ToDictionary(
            name => name,
            GetCurrentToolConnection,
            StringComparer.OrdinalIgnoreCase);
        if (normalizedConnection is "A" or "B")
        {
            var countAfterChange = _toolConnectionStateByName
                .Where(pair => !names.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .Count(pair => string.Equals(NormalizeDesktopConnection(pair.Value), normalizedConnection, StringComparison.OrdinalIgnoreCase)) + names.Length;
            if (countAfterChange > _toolConnectionLimit)
            {
                ShowToolVisibilityFeedback(
                    $"Connection {normalizedConnection} is full",
                    $"Connection {normalizedConnection} can expose at most {_toolConnectionLimit} enabled tools.",
                    UiInfoBarSeverity.Error,
                    autoClose: false);
                RestoreToolConnections(previousConnections);
                return;
            }
        }

        _toolMode = "custom";
        _ignoreToolCheckboxChanges = true;
        foreach (var name in names)
        {
            _toolConnectionStateByName[name] = normalizedConnection;
            if (_toolConnectionsByName.TryGetValue(name, out var combo))
            {
                combo.SelectedItem = normalizedConnection;
                combo.ToolTip = normalizedConnection;
            }
            if (_toolCheckboxesByName.TryGetValue(name, out var checkbox))
                checkbox.IsChecked = normalizedConnection is "A" or "B";
        }
        _ignoreToolCheckboxChanges = false;
        if (HasActiveToolFilter() || names.Length > 1)
            RenderCurrentToolVisibilityPreservingState();
        else
            UpdateToolStatusText();
    }

    private void UpdateToolStatusText(int? totalFromSnapshot = null)
    {
        if (_toolStatusText is null)
            return;

        _toolStatusText.Text = $"Connection A: {CountAssignedTo("A")} / {_toolConnectionLimit}   Connection B: {CountAssignedTo("B")} / {_toolConnectionLimit}";
        if (_toolTotalStatusText is not null)
        {
            var total = totalFromSnapshot ?? _toolConnectionStateByName.Count;
            var showing = _visibleToolNames.Length == 0 && total == 0 ? 0 : _visibleToolNames.Length;
            var local = CountToolsBySource("local");
            var external = CountToolsBySource("external");
            _toolTotalStatusText.Text = $"Showing {showing} / {total} tools ({local} local, {external} external)";
        }
    }

    private string[] GetSelectedToolNames() =>
        _toolSelectionByName
            .Where(pair => pair.Value.IsChecked == true)
            .Select(pair => pair.Key)
            .ToArray();

    private int CountAssignedTo(string connection) =>
        _toolConnectionStateByName.Values.Count(value =>
            string.Equals(NormalizeDesktopConnection(value), connection, StringComparison.OrdinalIgnoreCase));

    private int CountToolsBySource(string source) =>
        _toolSourceByName.Values.Count(value =>
            string.Equals(value, source, StringComparison.OrdinalIgnoreCase));

    private string GetToolConnection(string toolName) => GetCurrentToolConnection(toolName);

    private void RestoreToolConnections(IReadOnlyDictionary<string, string> previousConnections)
    {
        _ignoreToolCheckboxChanges = true;
        foreach (var pair in previousConnections)
        {
            var connection = NormalizeDesktopConnection(pair.Value);
            _toolConnectionStateByName[pair.Key] = connection;
            if (_toolConnectionsByName.TryGetValue(pair.Key, out var combo))
            {
                combo.SelectedItem = connection;
                combo.ToolTip = connection;
            }
            if (_toolCheckboxesByName.TryGetValue(pair.Key, out var checkbox))
                checkbox.IsChecked = connection is "A" or "B";
        }
        _ignoreToolCheckboxChanges = false;
        UpdateToolStatusText();
    }

    private static string NormalizeDesktopConnection(string? connection) =>
        string.Equals(connection, "A", StringComparison.OrdinalIgnoreCase)
            ? "A"
            : string.Equals(connection, "B", StringComparison.OrdinalIgnoreCase)
                ? "B"
                : "None";

    private async Task ApplyToolVisibilityAsync(bool restartServices)
    {
        try
        {
            SetToolVisibilityBusy(true);
            var assignments = _toolConnectionStateByName
                .Where(pair => NormalizeDesktopConnection(pair.Value) is "A" or "B")
                .ToDictionary(
                    pair => pair.Key,
                    pair => NormalizeDesktopConnection(pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            var enabledTools = assignments.Keys
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var response = await _toolVisibilityHttpClient.PutAsJsonAsync(
                BuildToolVisibilityEndpoint(),
                new ToolVisibilityDesktopUpdateRequest("custom", enabledTools, assignments),
                ToolVisibilityJsonOptions);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<ToolVisibilityErrorResponse>(ToolVisibilityJsonOptions);
                throw new InvalidOperationException(error?.Message ?? $"Gateway rejected the tool profile with HTTP {(int)response.StatusCode}.");
            }

            var snapshot = await response.Content.ReadFromJsonAsync<ToolVisibilityDesktopSnapshot>(ToolVisibilityJsonOptions);
            if (snapshot is not null)
                RenderToolVisibility(snapshot);

            if (restartServices)
            {
                await _supervisor.RestartAsync();
                ApplySupervisorSnapshot(_supervisor.Current);
            }

            ShowToolVisibilityFeedback(
                restartServices ? "Tool profile applied and services restarted" : "Tool profile applied",
                "Refresh/reconnect MCP tools in ChatGPT so it sees the new list.",
                UiInfoBarSeverity.Success,
                autoClose: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            await DesktopLog.WriteAsync("Tool visibility apply failed.", ex);
            ShowToolVisibilityFeedback(
                "Could not apply tool profile",
                ex.Message,
                UiInfoBarSeverity.Error,
                autoClose: false);
        }
        finally
        {
            SetToolVisibilityBusy(false);
        }
    }

    private string BuildToolVisibilityEndpoint()
    {
        var gatewayUrl = string.IsNullOrWhiteSpace(_supervisor.Current.GatewayUrl)
            ? "http://127.0.0.1:5227"
            : _supervisor.Current.GatewayUrl.TrimEnd('/');
        return $"{gatewayUrl}/api/tools/visibility";
    }

    private void SetToolVisibilityBusy(bool isBusy)
    {
        if (_toolRefreshButton is not null) _toolRefreshButton.IsEnabled = !isBusy;
        if (_toolSourceFilterBox is not null) _toolSourceFilterBox.IsEnabled = !isBusy;
        if (_toolShardFilterBox is not null) _toolShardFilterBox.IsEnabled = !isBusy;
        if (_toolMoveSelectedToAButton is not null) _toolMoveSelectedToAButton.IsEnabled = !isBusy;
        if (_toolMoveSelectedToBButton is not null) _toolMoveSelectedToBButton.IsEnabled = !isBusy;
        if (_toolDisableSelectedButton is not null) _toolDisableSelectedButton.IsEnabled = !isBusy;
        if (_toolApplyButton is not null) _toolApplyButton.IsEnabled = !isBusy;
        if (_toolApplyRestartButton is not null) _toolApplyRestartButton.IsEnabled = !isBusy;
        if (_toolGroupsPanel is not null) _toolGroupsPanel.IsEnabled = !isBusy;
    }

    private void ShowToolVisibilityFeedback(
        string title,
        string message,
        UiInfoBarSeverity severity,
        bool autoClose)
    {
        if (_toolInfoBar is null)
            return;

        _feedbackTimer.Stop();
        _toolInfoBar.Title = title;
        _toolInfoBar.Message = message;
        _toolInfoBar.Severity = severity;
        _toolInfoBar.IsOpen = true;

        if (autoClose)
            _feedbackTimer.Start();
    }

    private sealed record ToolVisibilityDesktopUpdateRequest(
        string Mode,
        IReadOnlyList<string> EnabledTools,
        IReadOnlyDictionary<string, string> ToolAssignments);

    private sealed class ToolVisibilityDesktopSnapshot
    {
        public string Mode { get; set; } = "all";
        public int ActiveCount { get; set; }
        public int TotalCount { get; set; }
        public string[] EnabledTools { get; set; } = Array.Empty<string>();
        public Dictionary<string, string> ToolAssignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ConnectionCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int MaxEnabledToolsPerConnection { get; set; } = 150;
        public ToolVisibilityExternalServer[] ExternalServers { get; set; } = Array.Empty<ToolVisibilityExternalServer>();
        public ToolVisibilityDesktopGroup[] Groups { get; set; } = Array.Empty<ToolVisibilityDesktopGroup>();
        public string ConfigPath { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed class ToolVisibilityDesktopGroup
    {
        public string Name { get; set; } = "Other";
        public int ActiveCount { get; set; }
        public int TotalCount { get; set; }
        public ToolVisibilityDesktopTool[] Tools { get; set; } = Array.Empty<ToolVisibilityDesktopTool>();
    }

    private sealed class ToolVisibilityDesktopTool
    {
        public string Name { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public string Risk { get; set; } = "safe";
        public bool Enabled { get; set; }
        public string Connection { get; set; } = "None";
        public string? Assignment { get; set; }
        public string? Shard { get; set; }
    }

    private sealed class ToolVisibilityErrorResponse
    {
        public string Error { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    private sealed class ToolVisibilityExternalServer
    {
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int ToolCount { get; set; }
    }
}
