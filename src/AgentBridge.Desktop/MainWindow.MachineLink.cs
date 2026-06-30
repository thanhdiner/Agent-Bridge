using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using AgentBridge.Desktop.Services;
using Wpf.Ui.Controls;

namespace AgentBridge.Desktop;

public partial class MainWindow
{
    private bool _isLinkingThisComputer;

    private async void ActivateDevice_Click(object sender, RoutedEventArgs e) =>
        await LinkThisComputerAsync();

    private async Task LinkThisComputerAsync()
    {
        if (_isLinkingThisComputer)
            return;

        var snapshot = _supervisor.Current;
        if (string.IsNullOrWhiteSpace(snapshot.DeviceId))
        {
            ShowOverviewFeedback(
                "Device not ready",
                "Wait for the Windows Agent to finish connecting, then activate this computer.",
                InfoBarSeverity.Warning,
                autoClose: true);
            return;
        }

        _isLinkingThisComputer = true;
        ActivateDeviceButton.IsEnabled = false;

        try
        {
            var store = new MachineLinkStore();
            var linkValue = await store.LoadOrCreateAsync();
            var service = new MachineLinkService();
            var result = await service.LinkAsync(
                snapshot.GatewayUrl,
                "dev-account",
                snapshot.DeviceId,
                "This computer",
                "dev",
                linkValue);

            ActivationStatusText.Text = $"Activated • {result.Plan}";
            ActivationStatusText.Foreground = GetResourceBrush("AgentBridgeSuccessBrush");
            ActivateDeviceButton.Content = "Re-activate";

            ShowOverviewFeedback(
                "This computer is activated",
                $"Device {result.DeviceId} is linked for the {result.Plan} plan.",
                InfoBarSeverity.Success,
                autoClose: true);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException
                                      or TaskCanceledException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or InvalidOperationException
                                      or System.Text.Json.JsonException)
        {
            await DesktopLog.WriteAsync("Device activation failed.", ex);
            ActivationStatusText.Text = "Activation failed";
            ActivationStatusText.Foreground = GetResourceBrush("AgentBridgeDangerBrush");
            ShowOverviewFeedback(
                "Activation failed",
                ex.Message,
                InfoBarSeverity.Error,
                autoClose: false);
        }
        finally
        {
            _isLinkingThisComputer = false;
            ActivateDeviceButton.IsEnabled = true;
        }
    }
}
