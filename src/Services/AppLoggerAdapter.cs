namespace EverythingDiskUsage.Services;

public sealed class AppLoggerAdapter : IAppLogger
{
    public string LogDirectory => AppLogger.LogDirectory;

    public string LogFilePath => AppLogger.LogFilePath;

    public bool LogEachSdkFile => AppLogger.LogEachSdkFile;

    public AppLogLevel MinimumLogLevel => AppLogger.MinimumLogLevel;

    public bool LogToDebugOutput => AppLogger.LogToDebugOutput;

    public int RetainedLogFiles => AppLogger.RetainedLogFiles;

    public AppSettings CurrentSettings => AppLogger.CurrentSettings;

    public void ApplySettings(AppSettings settings, string source) => AppLogger.ApplySettings(settings, source);

    public void Trace(string message) => AppLogger.Trace(message);

    public void Debug(string message) => AppLogger.Debug(message);

    public void Info(string message) => AppLogger.Info(message);

    public void Warning(string message) => AppLogger.Warning(message);

    public void Error(string message, Exception? exception = null) => AppLogger.Error(message, exception);

    public void Critical(string message, Exception? exception = null) => AppLogger.Critical(message, exception);

    public IDisposable TimedOperation(string name, AppLogLevel level = AppLogLevel.Info) => AppLogger.TimedOperation(name, level);
}
