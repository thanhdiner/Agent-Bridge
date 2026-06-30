using LocalMcp.BuildingBlocks.Configuration;

namespace LocalMcp.UnitTests;

public sealed class LocalConfigurationPathsTests
{
    [Fact]
    public void Application_Data_Paths_Share_The_Same_Root()
    {
        var root = LocalConfigurationPaths.GetApplicationDataDirectory();

        Assert.Equal(root, Path.GetDirectoryName(LocalConfigurationPaths.GetConfigurationFilePath()));
        Assert.Equal(root, Path.GetDirectoryName(LocalConfigurationPaths.GetDeviceFilePath()));
        Assert.Equal(root, Path.GetDirectoryName(LocalConfigurationPaths.GetLogsDirectory()));
    }

    [Fact]
    public void Application_Data_Paths_Use_Expected_Names()
    {
        Assert.Equal("config.json", Path.GetFileName(LocalConfigurationPaths.GetConfigurationFilePath()));
        Assert.Equal("device.json", Path.GetFileName(LocalConfigurationPaths.GetDeviceFilePath()));
        Assert.Equal("logs", Path.GetFileName(LocalConfigurationPaths.GetLogsDirectory()));
    }
}
