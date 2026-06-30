namespace LocalMcp.BuildingBlocks.Configuration;

public static class LocalConfigurationPaths
{
    public const string ApplicationDirectoryName = "AgentBridge";
    public const string ConfigurationFileName = "config.json";
    public const string DeviceFileName = "device.json";
    public const string RuntimeTokenFileName = "runtime-token.bin";
    public const string PreferredDeviceFileName = "preferred-device.json";
    public const string LogsDirectoryName = "logs";

    public static string GetApplicationDataDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        return Path.Combine(localApplicationData, ApplicationDirectoryName);
    }

    public static string GetConfigurationFilePath() =>
        Path.Combine(GetApplicationDataDirectory(), ConfigurationFileName);

    public static string GetDeviceFilePath() =>
        Path.Combine(GetApplicationDataDirectory(), DeviceFileName);

    public static string GetRuntimeTokenFilePath() =>
        Path.Combine(GetApplicationDataDirectory(), RuntimeTokenFileName);

    public static string GetPreferredDeviceFilePath() =>
        Path.Combine(GetApplicationDataDirectory(), PreferredDeviceFileName);

    public static string GetLogsDirectory() =>
        Path.Combine(GetApplicationDataDirectory(), LogsDirectoryName);
}
