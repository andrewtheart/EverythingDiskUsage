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
    public void Normalize_ReplacesInvalidThemeModeWithAuto()
    {
        var normalized = AppSettingsService.Normalize(new AppSettings { ThemeMode = (AppThemeMode)999 });

        Assert.Equal(AppThemeMode.Auto, normalized.ThemeMode);
    }

    [Theory]
    [InlineData(5, 30)]
    [InlineData(120, 120)]
    [InlineData(900, 600)]
    public void Normalize_ClampsFoundryInferenceTimeout(int input, int expected)
    {
        var normalized = AppSettingsService.Normalize(new AppSettings { FoundryInferenceTimeoutSeconds = input });

        Assert.Equal(expected, normalized.FoundryInferenceTimeoutSeconds);
    }

    [Fact]
    public void Normalize_ClearsQualificationWhenSelectedModelChanges()
    {
        var normalized = AppSettingsService.Normalize(new AppSettings
        {
            FoundryModelAlias = "phi-4-mini",
            FoundryQualifiedModelAlias = "phi-3.5-mini",
            FoundryQualificationUtc = DateTimeOffset.UtcNow
        });

        Assert.Null(normalized.FoundryQualifiedModelAlias);
        Assert.Null(normalized.FoundryQualificationUtc);
    }

    [Theory]
    [InlineData(AppThemeMode.Auto, AppThemeMode.Light)]
    [InlineData(AppThemeMode.Light, AppThemeMode.Dark)]
    [InlineData(AppThemeMode.Dark, AppThemeMode.Auto)]
    public void GetNextMode_CyclesAllThemeModes(AppThemeMode current, AppThemeMode expected)
    {
        Assert.Equal(expected, AppThemeService.GetNextMode(current));
    }

    [Theory]
    [InlineData(AppThemeMode.Auto, AppTheme.Light, AppTheme.Light)]
    [InlineData(AppThemeMode.Auto, AppTheme.Dark, AppTheme.Dark)]
    [InlineData(AppThemeMode.Light, AppTheme.Dark, AppTheme.Light)]
    [InlineData(AppThemeMode.Dark, AppTheme.Light, AppTheme.Dark)]
    public void Resolve_UsesSystemThemeOnlyForAuto(AppThemeMode mode, AppTheme systemTheme, AppTheme expected)
    {
        Assert.Equal(expected, AppThemeService.Resolve(mode, systemTheme));
    }

    [Fact]
    public void Clone_ReturnsIndependentCopy()
    {
        var original = new AppSettings
        {
            MinimumLogLevel = AppLogLevel.Debug,
            ThemeMode = AppThemeMode.Dark,
            LogEachSdkFile = true,
            LogToDebugOutput = true,
            RetainedLogFiles = 7,
            FoundryEnabled = true,
            FoundryModelAlias = "phi-4-mini",
            FoundryInferenceTimeoutSeconds = 240,
            FoundryQualifiedModelAlias = "phi-4-mini",
            FoundryQualificationUtc = DateTimeOffset.UtcNow
        };

        var clone = original.Clone();
        clone.RetainedLogFiles = 99;

        Assert.Equal(AppLogLevel.Debug, clone.MinimumLogLevel);
        Assert.Equal(AppThemeMode.Dark, clone.ThemeMode);
        Assert.True(clone.LogEachSdkFile);
        Assert.True(clone.LogToDebugOutput);
        Assert.True(clone.FoundryEnabled);
        Assert.Equal("phi-4-mini", clone.FoundryModelAlias);
        Assert.Equal(240, clone.FoundryInferenceTimeoutSeconds);
        Assert.Equal("phi-4-mini", clone.FoundryQualifiedModelAlias);
        Assert.Equal(original.FoundryQualificationUtc, clone.FoundryQualificationUtc);
        Assert.Equal(7, original.RetainedLogFiles);
    }
}