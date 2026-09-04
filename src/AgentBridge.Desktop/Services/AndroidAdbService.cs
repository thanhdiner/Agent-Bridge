using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AgentBridge.Desktop.Services;

internal sealed partial class AndroidAdbService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    public string? ResolveAdbPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = configuredPath.Trim().Trim('"');
            if (Path.IsPathFullyQualified(configured) && File.Exists(configured))
                return Path.GetFullPath(configured);

            var fromPath = FindOnPath(configured);
            if (fromPath is not null)
                return fromPath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(localAppData, "Android", "platform-tools", "adb.exe"),
            Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe")
        };

        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ANDROID_HOME"),
                     Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT")
                 }.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            candidates.Add(Path.Combine(root!, "platform-tools", "adb.exe"));
        }

        return candidates.FirstOrDefault(File.Exists)
            ?? FindOnPath("adb.exe")
            ?? FindOnPath("adb");
    }

    public async Task<string> GetVersionAsync(string adbPath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(adbPath, ["version"], null, cancellationToken);
        EnsureSuccess(result, "ADB version check failed");
        return FirstNonEmptyLine(result.StandardOutput) ?? "ADB is ready";
    }

    public async Task<string> PairAsync(
        string adbPath,
        string deviceIp,
        int pairingPort,
        string pairingCode,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(deviceIp, pairingPort);
        if (!PairingCodeRegex().IsMatch(pairingCode))
            throw new ArgumentException("Pairing code must contain exactly 6 digits.", nameof(pairingCode));

        var result = await RunAsync(adbPath, ["pair", endpoint], pairingCode + Environment.NewLine, cancellationToken);
        EnsureSuccess(result, "ADB pairing failed");
        var output = CombineOutput(result);
        if (!output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("already paired", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output)
                ? "ADB did not confirm that pairing succeeded."
                : output);
        }

        return output;
    }

    public async Task<string> ConnectAsync(
        string adbPath,
        string deviceIp,
        int connectionPort,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(deviceIp, connectionPort);
        var result = await RunAsync(adbPath, ["connect", endpoint], null, cancellationToken);
        EnsureSuccess(result, "ADB connection failed");
        var output = CombineOutput(result);
        if (output.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || output.Contains("cannot", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(output);
        }

        return output;
    }

    public async Task<string> DisconnectAsync(
        string adbPath,
        string deviceIp,
        int connectionPort,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(deviceIp, connectionPort);
        var result = await RunAsync(adbPath, ["disconnect", endpoint], null, cancellationToken);
        EnsureSuccess(result, "ADB disconnect failed");
        return CombineOutput(result);
    }

    public async Task<AndroidAdbSnapshot> InspectAsync(
        string adbPath,
        string? preferredIp,
        int? preferredConnectionPort,
        CancellationToken cancellationToken = default)
    {
        var version = await GetVersionAsync(adbPath, cancellationToken);
        var devicesResult = await RunAsync(adbPath, ["devices", "-l"], null, cancellationToken);
        EnsureSuccess(devicesResult, "Could not list ADB devices");

        var devices = ParseDevices(devicesResult.StandardOutput);
        var services = await TryDiscoverServicesAsync(adbPath, cancellationToken);
        var preferredEndpoint = TryBuildEndpoint(preferredIp, preferredConnectionPort);
        var selected = SelectDevice(devices, preferredEndpoint, preferredIp);
        var discoveredConnection = services
            .FirstOrDefault(service => service.Kind == AndroidAdbServiceKind.Connection
                && (string.IsNullOrWhiteSpace(preferredIp)
                    || service.Endpoint.StartsWith(preferredIp + ":", StringComparison.OrdinalIgnoreCase)));
        var discoveredPairing = services
            .FirstOrDefault(service => service.Kind == AndroidAdbServiceKind.Pairing
                && (string.IsNullOrWhiteSpace(preferredIp)
                    || service.Endpoint.StartsWith(preferredIp + ":", StringComparison.OrdinalIgnoreCase)));

        if (selected is null)
        {
            return new AndroidAdbSnapshot(
                version,
                null,
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                discoveredPairing?.Endpoint,
                discoveredConnection?.Endpoint,
                devices);
        }

        var serial = selected.Serial;
        var manufacturer = await TryRunDeviceTextAsync(adbPath, serial, ["shell", "getprop", "ro.product.manufacturer"], cancellationToken);
        var model = await TryRunDeviceTextAsync(adbPath, serial, ["shell", "getprop", "ro.product.model"], cancellationToken);
        var versionText = await TryRunDeviceTextAsync(adbPath, serial, ["shell", "getprop", "ro.build.version.release"], cancellationToken);
        var screenSize = await TryRunDeviceTextAsync(adbPath, serial, ["shell", "wm", "size"], cancellationToken);
        var adbEnabled = await TryRunDeviceTextAsync(adbPath, serial, ["shell", "settings", "get", "global", "adb_enabled"], cancellationToken);
        var wifiEnabled = await TryRunDeviceTextAsync(adbPath, serial, ["shell", "settings", "get", "global", "adb_wifi_enabled"], cancellationToken);
        var secureInput = await TryRunDeviceTextAsync(adbPath, serial, ["shell", "getprop", "persist.security.adbinput"], cancellationToken);

        return new AndroidAdbSnapshot(
            version,
            serial,
            string.Equals(selected.State, "device", StringComparison.OrdinalIgnoreCase),
            selected.State,
            manufacturer,
            model,
            versionText,
            screenSize.Replace("Physical size:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(),
            FormatDebugFlags(adbEnabled, wifiEnabled, secureInput),
            discoveredPairing?.Endpoint,
            discoveredConnection?.Endpoint,
            devices);
    }

    internal static string BuildEndpoint(string deviceIp, int port)
    {
        if (!IPAddress.TryParse(deviceIp?.Trim(), out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Enter the IPv4 address shown on the phone's Wireless debugging screen.", nameof(deviceIp));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        return $"{address}:{port}";
    }

    internal static IReadOnlyList<AndroidAdbDevice> ParseDevices(string output)
    {
        var devices = new List<AndroidAdbDevice>();
        foreach (var line in SplitLines(output).Skip(1))
        {
            var match = DeviceLineRegex().Match(line);
            if (!match.Success)
                continue;
            devices.Add(new AndroidAdbDevice(match.Groups["serial"].Value, match.Groups["state"].Value, line.Trim()));
        }

        return devices;
    }

    internal static IReadOnlyList<AndroidAdbDiscoveredService> ParseServices(string output)
    {
        var services = new List<AndroidAdbDiscoveredService>();
        foreach (var line in SplitLines(output))
        {
            var match = ServiceLineRegex().Match(line);
            if (!match.Success)
                continue;
            var kind = match.Groups["kind"].Value.Equals("pairing", StringComparison.OrdinalIgnoreCase)
                ? AndroidAdbServiceKind.Pairing
                : AndroidAdbServiceKind.Connection;
            services.Add(new AndroidAdbDiscoveredService(kind, match.Groups["endpoint"].Value));
        }

        return services;
    }

    private static async Task<IReadOnlyList<AndroidAdbDiscoveredService>> TryDiscoverServicesAsync(
        string adbPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAsync(adbPath, ["mdns", "services"], null, cancellationToken);
            return result.ExitCode == 0 ? ParseServices(result.StandardOutput) : [];
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return [];
        }
    }

    private static AndroidAdbDevice? SelectDevice(
        IReadOnlyList<AndroidAdbDevice> devices,
        string? preferredEndpoint,
        string? preferredIp)
    {
        var online = devices.Where(device => string.Equals(device.State, "device", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!string.IsNullOrWhiteSpace(preferredEndpoint))
        {
            var exact = online.FirstOrDefault(device => string.Equals(device.Serial, preferredEndpoint, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;
        }

        if (!string.IsNullOrWhiteSpace(preferredIp))
        {
            var sameIp = online.FirstOrDefault(device => device.Serial.StartsWith(preferredIp + ":", StringComparison.OrdinalIgnoreCase));
            if (sameIp is not null)
                return sameIp;
        }

        return online.FirstOrDefault(device => Ipv4EndpointRegex().IsMatch(device.Serial))
            ?? online.FirstOrDefault();
    }

    private static string? TryBuildEndpoint(string? ip, int? port)
    {
        if (string.IsNullOrWhiteSpace(ip) || port is null)
            return null;
        try
        {
            return BuildEndpoint(ip, port.Value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static async Task<string> TryRunDeviceTextAsync(
        string adbPath,
        string serial,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken)
    {
        try
        {
            var arguments = new List<string> { "-s", serial };
            arguments.AddRange(command);
            var result = await RunAsync(adbPath, arguments, null, cancellationToken);
            return result.ExitCode == 0 ? result.StandardOutput.Trim() : string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return string.Empty;
        }
    }

    private static string FormatDebugFlags(string adbEnabled, string wifiEnabled, string secureInput)
    {
        static string Flag(string value) => value.Trim() == "1" ? "On" : value.Trim() == "0" ? "Off" : "Unknown";
        return $"USB debugging: {Flag(adbEnabled)}  ·  Wireless: {Flag(wifiEnabled)}  ·  Secure input: {Flag(secureInput)}";
    }

    private static async Task<AdbCommandResult> RunAsync(
        string adbPath,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = adbPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("ADB could not be started.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"ADB executable '{adbPath}' was not found or could not be started.", ex);
        }

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        using var timeout = new CancellationTokenSource(CommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var outputTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var errorTask = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            return new AdbCommandResult(process.ExitCode, (await outputTask).Trim(), (await errorTask).Trim());
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("ADB command exceeded 20 seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void EnsureSuccess(AdbCommandResult result, string operation)
    {
        if (result.ExitCode == 0)
            return;
        var detail = CombineOutput(result);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? operation : $"{operation}: {detail}");
    }

    private static string CombineOutput(AdbCommandResult result) =>
        string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

    private static string? FirstNonEmptyLine(string value) => SplitLines(value).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();

    private static IEnumerable<string> SplitLines(string value) => value.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    [GeneratedRegex("^\\s*(?<serial>\\S+)\\s+(?<state>device|offline|unauthorized|no permissions)(?:\\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceLineRegex();

    [GeneratedRegex("_adb-tls-(?<kind>pairing|connect)\\._tcp\\.?\\s+(?<endpoint>(?:\\d{1,3}\\.){3}\\d{1,3}:\\d{1,5})(?:\\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceLineRegex();

    [GeneratedRegex("^(?:\\d{1,3}\\.){3}\\d{1,3}:\\d{1,5}$")]
    private static partial Regex Ipv4EndpointRegex();

    [GeneratedRegex("^\\d{6}$")]
    private static partial Regex PairingCodeRegex();
}

internal sealed record AndroidAdbSnapshot(
    string AdbVersion,
    string? Serial,
    bool Connected,
    string State,
    string Manufacturer,
    string Model,
    string AndroidVersion,
    string ScreenSize,
    string DebugFlags,
    string? DiscoveredPairingEndpoint,
    string? DiscoveredConnectionEndpoint,
    IReadOnlyList<AndroidAdbDevice> Devices);

internal sealed record AndroidAdbDevice(string Serial, string State, string Detail);

internal enum AndroidAdbServiceKind
{
    Pairing,
    Connection
}

internal sealed record AndroidAdbDiscoveredService(AndroidAdbServiceKind Kind, string Endpoint);

internal sealed record AdbCommandResult(int ExitCode, string StandardOutput, string StandardError);
