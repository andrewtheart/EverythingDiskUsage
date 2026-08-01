using Microsoft.Win32;

namespace EverythingDiskUsage.Services;

public enum AppThemeMode
{
    Auto,
    Light,
    Dark
}

public enum AppTheme
{
    Light,
    Dark
}

public static class AppThemeService
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static AppTheme Resolve(AppThemeMode mode)
    {
        return Resolve(mode, GetSystemTheme());
    }

    public static AppTheme Resolve(AppThemeMode mode, AppTheme systemTheme)
    {
        return mode switch
        {
            AppThemeMode.Light => AppTheme.Light,
            AppThemeMode.Dark => AppTheme.Dark,
            _ => systemTheme
        };
    }

    public static AppThemeMode GetNextMode(AppThemeMode mode)
    {
        return mode switch
        {
            AppThemeMode.Auto => AppThemeMode.Light,
            AppThemeMode.Light => AppThemeMode.Dark,
            _ => AppThemeMode.Auto
        };
    }

    private static AppTheme GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? AppTheme.Dark
                : AppTheme.Light;
        }
        catch
        {
            return AppTheme.Light;
        }
    }
}