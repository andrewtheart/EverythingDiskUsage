using EverythingDiskUsage.Services;

namespace EverythingDiskUsage.Tests.Services;

public sealed class AppSettingsTests
{
    [Theory]
    [InlineData(-20, 1)]
    [InlineData(0, 1)]
    [InlineData(20, 20)]
    [InlineData(700, 500)]
    public void Normalize_ClampsRetainedLogFiles(int input, int expected)
    {
        var normalized = AppSettingsService.Normalize(new AppSettings { RetainedLogFiles = input });

        Assert.Equal(expected, normalized.RetainedLogFiles);
    }

    [Fact]
    public void Normalize_ReplacesInvalidLogLevelWithInfo()
    {
        var normalized = AppSettingsService.Normalize(new AppSettings { MinimumLogLevel = (AppLogLevel)999 });

        Assert.Equal(AppLogLevel.Info, normalized.MinimumLogLevel);
    }

    [Fact]
    public void Clone_ReturnsIndependentCopy()
    {
        var original = new AppSettings
        {
            MinimumLogLevel = AppLogLevel.Debug,
            LogEachSdkFile = true,
            LogToDebugOutput = true,
            RetainedLogFiles = 7
        };

        var clone = original.Clone();
        clone.RetainedLogFiles = 99;

        Assert.Equal(AppLogLevel.Debug, clone.MinimumLogLevel);
        Assert.True(clone.LogEachSdkFile);
        Assert.True(clone.LogToDebugOutput);
        Assert.Equal(7, original.RetainedLogFiles);
    }
}