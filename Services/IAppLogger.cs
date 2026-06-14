namespace EverythingDiskUsage.Services;

public interface IAppLogger
{
    string LogDirectory { get; }

    string LogFilePath { get; }

    bool LogEachSdkFile { get; }

    AppLogLevel MinimumLogLevel { get; }

    bool LogToDebugOutput { get; }

    int RetainedLogFiles { get; }

    AppSettings CurrentSettings { get; }

    void ApplySettings(AppSettings settings, string source);

    void Trace(string message);

    void Debug(string message);

    void Info(string message);

    void Warning(string message);

    void Error(string message, Exception? exception = null);

    void Critical(string message, Exception? exception = null);

    IDisposable TimedOperation(string name, AppLogLevel level = AppLogLevel.Info);
}
