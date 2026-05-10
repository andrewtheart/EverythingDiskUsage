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
    private const int RetainedLogFiles = 20;
    private static readonly object SyncRoot = new();

    static AppLogger()
    {
        LogDirectory = ResolveLogDirectory();
        Directory.CreateDirectory(LogDirectory);

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        LogFilePath = Path.Combine(LogDirectory, $"EverythingDiskUsage-{timestamp}-{Environment.ProcessId}.log");
        LogEachSdkFile = IsEnabled("EVERYTHING_DISK_USAGE_LOG_EACH_FILE");

        TryDeleteOldLogs();
        Info("Logger initialized");
        Info($"ProcessId={Environment.ProcessId}; Machine='{Environment.MachineName}'; User='{Environment.UserName}'; OS='{Environment.OSVersion}'; 64BitProcess={Environment.Is64BitProcess}; Framework='{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}'");
        Info($"Executable='{Environment.ProcessPath ?? string.Empty}'; CurrentDirectory='{Environment.CurrentDirectory}'; BaseDirectory='{AppContext.BaseDirectory}'");
        Info($"AssemblyVersion='{Assembly.GetExecutingAssembly().GetName().Version}'");
        Info($"LogEachSdkFile={LogEachSdkFile}");
    }

    public static string LogDirectory { get; }

    public static string LogFilePath { get; }

    public static bool LogEachSdkFile { get; }

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

    private static void Write(AppLogLevel level, string message, Exception? exception = null)
    {
        try
        {
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
            var logFiles = Directory
                .EnumerateFiles(LogDirectory, "EverythingDiskUsage-*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(RetainedLogFiles)
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
