using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace EverythingDiskUsage.Services;

public enum AppLogLevel
{
    Trace,
    Debug,
    Info,
    Warning,
    Error,
    Critical
}

public static class AppLogger
{
    private static readonly object SyncRoot = new();
    private static AppSettings _settings = AppSettingsService.CreateDefault();
    private static AppLogLevel _minimumLogLevel = AppLogLevel.Info;
    private static bool _logToDebugOutput;
    private static int _retainedLogFiles = 20;

    static AppLogger()
    {
        ApplySettingsCore(AppSettingsService.Load());
        LogDirectory = ResolveLogDirectory();
        Directory.CreateDirectory(LogDirectory);

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        LogFilePath = Path.Combine(LogDirectory, $"EverythingDiskUsage-{timestamp}-{Environment.ProcessId}.log");

        TryDeleteOldLogs();
        Write(AppLogLevel.Info, "Logger initialized", force: true);
        Write(AppLogLevel.Info, $"ProcessId={Environment.ProcessId}; Machine='{Environment.MachineName}'; User='{Environment.UserName}'; OS='{Environment.OSVersion}'; 64BitProcess={Environment.Is64BitProcess}; Framework='{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}'", force: true);
        Write(AppLogLevel.Info, $"Executable='{Environment.ProcessPath ?? string.Empty}'; CurrentDirectory='{Environment.CurrentDirectory}'; BaseDirectory='{AppContext.BaseDirectory}'", force: true);
        Write(AppLogLevel.Info, $"AssemblyVersion='{Assembly.GetExecutingAssembly().GetName().Version}'", force: true);
        Write(AppLogLevel.Info, $"SettingsFile='{AppSettingsService.SettingsFilePath}'; LogDirectory='{LogDirectory}'; LogFile='{LogFilePath}'", force: true);
        Write(AppLogLevel.Info, $"Logger settings: MinimumLogLevel={MinimumLogLevel}; LogEachSdkFile={LogEachSdkFile}; LogToDebugOutput={LogToDebugOutput}; RetainedLogFiles={RetainedLogFiles}", force: true);
    }

    public static string LogDirectory { get; }

    public static string LogFilePath { get; }

    public static bool LogEachSdkFile { get; private set; }

    public static AppLogLevel MinimumLogLevel
    {
        get
        {
            lock (SyncRoot)
            {
                return _minimumLogLevel;
            }
        }
    }

    public static bool LogToDebugOutput
    {
        get
        {
            lock (SyncRoot)
            {
                return _logToDebugOutput;
            }
        }
    }

    public static int RetainedLogFiles
    {
        get
        {
            lock (SyncRoot)
            {
                return _retainedLogFiles;
            }
        }
    }

    public static AppSettings CurrentSettings
    {
        get
        {
            lock (SyncRoot)
            {
                return _settings.Clone();
            }
        }
    }

    public static void ApplySettings(AppSettings settings, string source)
    {
        ApplySettingsCore(settings);
        TryDeleteOldLogs();
        Write(AppLogLevel.Info, $"Logger settings applied; source='{source}'; MinimumLogLevel={MinimumLogLevel}; LogEachSdkFile={LogEachSdkFile}; LogToDebugOutput={LogToDebugOutput}; RetainedLogFiles={RetainedLogFiles}; SettingsFile='{AppSettingsService.SettingsFilePath}'", force: true);
    }

    public static void Trace(string message) => Write(AppLogLevel.Trace, message);

    public static void Debug(string message) => Write(AppLogLevel.Debug, message);

    public static void Info(string message) => Write(AppLogLevel.Info, message);

    public static void Warning(string message) => Write(AppLogLevel.Warning, message);

    public static void Error(string message, Exception? exception = null) => Write(AppLogLevel.Error, message, exception);

    public static void Critical(string message, Exception? exception = null) => Write(AppLogLevel.Critical, message, exception);

    public static IDisposable TimedOperation(string name, AppLogLevel level = AppLogLevel.Info)
    {
        Write(level, $"BEGIN {name}");
        return new TimedLogScope(name, level);
    }

    private static void Write(AppLogLevel level, string message, Exception? exception = null, bool force = false)
    {
        try
        {
            AppLogLevel minimumLogLevel;
            bool logToDebugOutput;
            lock (SyncRoot)
            {
                minimumLogLevel = _minimumLogLevel;
                logToDebugOutput = _logToDebugOutput;
            }

            if (!force && level < minimumLogLevel)
            {
                return;
            }

            var builder = new StringBuilder();
            builder.Append(DateTimeOffset.Now.ToString("o"));
            builder.Append('\t');
            builder.Append(level.ToString().ToUpperInvariant());
            builder.Append('\t');
            builder.Append("T");
            builder.Append(Environment.CurrentManagedThreadId.ToString("D2"));
            builder.Append('\t');
            builder.Append(Sanitize(message));

            if (exception is not null)
            {
                builder.AppendLine();
                builder.Append(Indent(exception.ToString()));
            }

            lock (SyncRoot)
            {
                File.AppendAllText(LogFilePath, builder.ToString() + Environment.NewLine, Encoding.UTF8);
            }

            if (logToDebugOutput)
            {
                System.Diagnostics.Trace.WriteLine(builder.ToString());
            }
        }
        catch
        {
            System.Diagnostics.Trace.WriteLine($"[{level}] {message}");
            if (exception is not null)
            {
                System.Diagnostics.Trace.WriteLine(exception);
            }
        }
    }

    private static string ResolveLogDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "EverythingDiskUsage", "logs");
        }

        return Path.Combine(Path.GetTempPath(), "EverythingDiskUsage", "logs");
    }

    private static void TryDeleteOldLogs()
    {
        try
        {
            int retainedLogFiles;
            lock (SyncRoot)
            {
                retainedLogFiles = _retainedLogFiles;
            }

            var logFiles = Directory
                .EnumerateFiles(LogDirectory, "EverythingDiskUsage-*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(retainedLogFiles)
                .ToList();

            foreach (var file in logFiles)
            {
                file.Delete();
            }
        }
        catch
        {
        }
    }

    private static void ApplySettingsCore(AppSettings settings)
    {
        var normalized = AppSettingsService.Normalize(settings);
        lock (SyncRoot)
        {
            _settings = normalized.Clone();
            _minimumLogLevel = normalized.MinimumLogLevel;
            LogEachSdkFile = normalized.LogEachSdkFile || IsEnabled("EVERYTHING_DISK_USAGE_LOG_EACH_FILE");
            _logToDebugOutput = normalized.LogToDebugOutput;
            _retainedLogFiles = normalized.RetainedLogFiles;
        }
    }

    private static bool IsEnabled(string environmentVariableName)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariableName);
        return value is not null &&
            (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static string Sanitize(string message)
    {
        return message.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string Indent(string text)
    {
        return "    " + text.Replace(Environment.NewLine, Environment.NewLine + "    ", StringComparison.Ordinal);
    }

    private sealed class TimedLogScope : IDisposable
    {
        private readonly string _name;
        private readonly AppLogLevel _level;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _disposed;

        public TimedLogScope(string name, AppLogLevel level)
        {
            _name = name;
            _level = level;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();
            Write(_level, $"END {_name}; elapsedMs={_stopwatch.ElapsedMilliseconds}");
        }
    }
}
