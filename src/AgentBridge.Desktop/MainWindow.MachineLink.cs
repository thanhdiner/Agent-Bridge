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
    private bool _isCheckingActivationStatus;

    private async void ActivateDevice_Click(object sender, RoutedEventArgs e) =>
        await LinkThisComputerAsync();

    private async Task RefreshActivationStatusAsync(bool showErrors)
    {
        if (_isCheckingActivationStatus)
            return;

        var snapshot = _supervisor.Current;
        if (string.IsNullOrWhiteSpace(snapshot.DeviceId))
        {
            ActivationStatusText.Text = "Device not ready";
            ActivationStatusText.Foreground = GetResourceBrush("AgentBridgeWarningBrush");
            ActivateDeviceButton.Content = "Activate this computer";
            return;
        }

        _isCheckingActivationStatus = true;
        ActivateDeviceButton.IsEnabled = false;

        try
        {
            var service = new MachineLinkService();
            var result = await service.GetStatusAsync(snapshot.GatewayUrl, snapshot.DeviceId);
            if (result is { Activated: true })
            {
                var licenseActive = IsLicenseActive(result);
                ActivationStatusText.Text = licenseActive
                    ? $"License active • Valid until: {FormatLicenseDate(result.ActiveUntilUtc)}"
                    : "License expired • Renew to continue using AgentBridge";
                ActivationStatusText.Foreground = GetResourceBrush(licenseActive ? "AgentBridgeSuccessBrush" : "AgentBridgeDangerBrush");
                ActivateDeviceButton.Content = "Re-activate";
            }
            else
            {
                ActivationStatusText.Text = "Device not activated";
                ActivationStatusText.Foreground = GetResourceBrush("AgentBridgeWarningBrush");
                ActivateDeviceButton.Content = "Activate this computer";

                if (showErrors)
                {
                    ShowOverviewFeedback(
                        "This computer is not activated",
                        "Activate this device to continue.",
                        InfoBarSeverity.Warning,
                        autoClose: false);
                }
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException
                                      or TaskCanceledException
                                      or InvalidOperationException
                                      or System.Text.Json.JsonException)
        {
            await DesktopLog.WriteAsync("Activation status refresh failed.", ex);
            ActivationStatusText.Text = "Activation unknown";
            ActivationStatusText.Foreground = GetResourceBrush("AgentBridgeWarningBrush");
            ActivateDeviceButton.Content = "Activate this computer";

            if (showErrors)
            {
                ShowOverviewFeedback(
                    "Could not check activation",
                    ex.Message,
                    InfoBarSeverity.Warning,
                    autoClose: false);
            }
        }
        finally
        {
            _isCheckingActivationStatus = false;
            ActivateDeviceButton.IsEnabled = !_isLinkingThisComputer;
        }
    }

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
                linkValue);

            ActivationStatusText.Text = $"License active • Valid until: {FormatLicenseDate(result.ActiveUntilUtc)}";
            ActivationStatusText.Foreground = GetResourceBrush("AgentBridgeSuccessBrush");
            ActivateDeviceButton.Content = "Re-activate";

            ShowOverviewFeedback(
                "This computer is activated",
                $"License active. Valid until: {FormatLicenseDate(result.ActiveUntilUtc)}.",
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

    private static bool IsLicenseActive(MachineLinkResponse response) =>
        string.Equals(response.Status, "active", StringComparison.OrdinalIgnoreCase) &&
        response.ActiveUntilUtc is { } activeUntilUtc &&
        activeUntilUtc > DateTimeOffset.UtcNow;

    private static string FormatLicenseDate(DateTimeOffset? activeUntilUtc) =>
        activeUntilUtc?.UtcDateTime.ToString("yyyy-MM-dd") ?? "unknown";
}
