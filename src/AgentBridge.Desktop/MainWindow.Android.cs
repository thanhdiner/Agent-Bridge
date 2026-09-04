using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UiButton = Wpf.Ui.Controls.Button;
using UiControlAppearance = Wpf.Ui.Controls.ControlAppearance;

namespace AgentBridge.Desktop;

public partial class MainWindow
{
    private UiButton? _androidNavButton;
    private AndroidSetupPage? _androidPage;

    private void InitializeAndroidUi()
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

        _androidNavButton = new UiButton
        {
            Content = "Android",
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Phone24 },
            Appearance = UiControlAppearance.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0)
        };
        AutomationProperties.SetAutomationId(_androidNavButton, "AndroidNavButton");
        _androidNavButton.Click += AndroidNav_Click;
        ApplyNavButtonStyle(_androidNavButton, false);
        navPanel.Children.Add(_androidNavButton);

        _androidPage = new AndroidSetupPage(() => _supervisor.Current.GatewayUrl)
        {
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(_androidPage, 1);
        shellGrid.Children.Add(_androidPage);
    }

    private async void AndroidNav_Click(object sender, RoutedEventArgs e)
    {
        ShowAndroid();
        if (_androidPage is not null)
            await _androidPage.ActivateAsync();
    }

    private void ShowAndroid()
    {
        OverviewPage.Visibility = Visibility.Collapsed;
        WorkspacePage.Visibility = Visibility.Collapsed;
        if (_toolPage is not null)
            _toolPage.Visibility = Visibility.Collapsed;
        if (_runtimePage is not null)
            _runtimePage.Visibility = Visibility.Collapsed;
        if (_androidPage is not null)
            _androidPage.Visibility = Visibility.Visible;

        ApplyNavButtonStyle(OverviewNavButton, false);
        ApplyNavButtonStyle(WorkspacesNavButton, false);
        ApplyNavButtonStyle(_toolsNavButton, false);
        ApplyNavButtonStyle(_runtimeNavButton, false);
        ApplyNavButtonStyle(_androidNavButton, true);
    }
}
