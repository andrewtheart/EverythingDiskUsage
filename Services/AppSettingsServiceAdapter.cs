namespace EverythingDiskUsage.Services;

public sealed class AppSettingsServiceAdapter : IAppSettingsService
{
    private readonly IAppLogger _logger;

    public AppSettingsServiceAdapter()
        : this(new AppLoggerAdapter())
    {
    }

    public AppSettingsServiceAdapter(IAppLogger logger)
    {
        _logger = logger;
    }

    public string SettingsDirectory => AppSettingsService.SettingsDirectory;

    public string SettingsFilePath => AppSettingsService.SettingsFilePath;

    public AppSettings Load()
    {
        using var operation = _logger.TimedOperation($"Load settings; path='{SettingsFilePath}'", AppLogLevel.Debug);
        var settings = AppSettingsService.Load();
        _logger.Info($"Settings loaded; path='{SettingsFilePath}'; minimumLogLevel={settings.MinimumLogLevel}; logEachSdkFile={settings.LogEachSdkFile}; logToDebugOutput={settings.LogToDebugOutput}; retainedLogFiles={settings.RetainedLogFiles}");
        return settings;
    }

    public void Save(AppSettings settings)
    {
        var normalized = AppSettingsService.Normalize(settings);
        using var operation = _logger.TimedOperation($"Save settings; path='{SettingsFilePath}'", AppLogLevel.Debug);
        AppSettingsService.Save(normalized);
        _logger.Info($"Settings saved; path='{SettingsFilePath}'; minimumLogLevel={normalized.MinimumLogLevel}; logEachSdkFile={normalized.LogEachSdkFile}; logToDebugOutput={normalized.LogToDebugOutput}; retainedLogFiles={normalized.RetainedLogFiles}");
    }

    public AppSettings CreateDefault()
    {
        var defaults = AppSettingsService.CreateDefault();
        _logger.Debug($"Default settings created; minimumLogLevel={defaults.MinimumLogLevel}; logEachSdkFile={defaults.LogEachSdkFile}; logToDebugOutput={defaults.LogToDebugOutput}; retainedLogFiles={defaults.RetainedLogFiles}");
        return defaults;
    }

    public AppSettings Normalize(AppSettings settings)
    {
        var normalized = AppSettingsService.Normalize(settings);
        _logger.Debug($"Settings normalized; inputMinimumLogLevel={settings.MinimumLogLevel}; outputMinimumLogLevel={normalized.MinimumLogLevel}; inputRetainedLogFiles={settings.RetainedLogFiles}; outputRetainedLogFiles={normalized.RetainedLogFiles}");
        return normalized;
    }
}
