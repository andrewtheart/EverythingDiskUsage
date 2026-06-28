using EverythingDiskUsage.Models;
using EverythingDiskUsage.Native;
using EverythingDiskUsage.Services;

namespace EverythingDiskUsage.Tests;

internal sealed class TestLogger : IAppLogger
{
    public List<string> Messages { get; } = [];

    public string LogDirectory { get; } = Path.Combine(Path.GetTempPath(), "EverythingDiskUsageTests", "logs");

    public string LogFilePath { get; } = Path.Combine(Path.GetTempPath(), "EverythingDiskUsageTests", "logs", "test.log");

    public bool LogEachSdkFile => false;

    public AppLogLevel MinimumLogLevel { get; private set; } = AppLogLevel.Trace;

    public bool LogToDebugOutput { get; private set; }

    public int RetainedLogFiles { get; private set; } = 20;

    public AppSettings CurrentSettings { get; private set; } = new() { MinimumLogLevel = AppLogLevel.Trace };

    public void ApplySettings(AppSettings settings, string source)
    {
        CurrentSettings = settings.Clone();
        MinimumLogLevel = CurrentSettings.MinimumLogLevel;
        LogToDebugOutput = CurrentSettings.LogToDebugOutput;
        RetainedLogFiles = CurrentSettings.RetainedLogFiles;
        Messages.Add($"ApplySettings:{source}");
    }

    public void Trace(string message) => Messages.Add("Trace:" + message);

    public void Debug(string message) => Messages.Add("Debug:" + message);

    public void Info(string message) => Messages.Add("Info:" + message);

    public void Warning(string message) => Messages.Add("Warning:" + message);

    public void Error(string message, Exception? exception = null) => Messages.Add("Error:" + message);

    public void Critical(string message, Exception? exception = null) => Messages.Add("Critical:" + message);

    public IDisposable TimedOperation(string name, AppLogLevel level = AppLogLevel.Info)
    {
        Messages.Add($"Timed:{name}");
        return new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal sealed class TestSettingsService : IAppSettingsService
{
    public TestSettingsService(AppSettings? settings = null)
    {
        SavedSettings = settings?.Clone() ?? new AppSettings();
        SettingsDirectory = Path.Combine(Path.GetTempPath(), "EverythingDiskUsageTests", Guid.NewGuid().ToString("N"));
        SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");
    }

    public string SettingsDirectory { get; }

    public string SettingsFilePath { get; }

    public AppSettings SavedSettings { get; private set; }

    public int SaveCount { get; private set; }

    public AppSettings Load() => SavedSettings.Clone();

    public void Save(AppSettings settings)
    {
        SavedSettings = Normalize(settings);
        SaveCount++;
    }

    public AppSettings CreateDefault() => new()
    {
        MinimumLogLevel = AppLogLevel.Info,
        LogEachSdkFile = false,
        LogToDebugOutput = false,
        RetainedLogFiles = 20
    };

    public AppSettings Normalize(AppSettings settings) => AppSettingsService.Normalize(settings);
}

internal sealed class TestShellContextMenuService : IShellContextMenuService
{
    public List<string> RequestedPaths { get; } = [];

    public ShellContextMenuResult Result { get; set; } = ShellContextMenuResult.None;

    public ShellContextMenuResult Show(
        IntPtr owner,
        string path,
        int screenX,
        int screenY,
        Func<ShellContextMenuCommand, bool>? shouldInvokeShell = null)
    {
        RequestedPaths.Add(path);
        return Result;
    }
}

internal sealed class ImmediateAnalyzer : IDiskUsageAnalyzer
{
    private readonly ScanResult _result;

    public ImmediateAnalyzer(ScanResult result)
    {
        _result = result;
    }

    public Task<ScanResult> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ScanProgress(_result.Root.FileCount, _result.TotalResults, _result.Root.SizeBytes));
        return Task.FromResult(_result);
    }
}

internal sealed class ControllableAnalyzer : IDiskUsageAnalyzer
{
    private readonly TaskCompletionSource<ScanResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationToken CapturedToken { get; private set; }

    public Task<ScanResult> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        CapturedToken = cancellationToken;
        progress?.Report(new ScanProgress(0, 10, 0));
        Started.TrySetResult(rootPath);
        return _completion.Task;
    }

    public void Complete(ScanResult result)
    {
        _completion.TrySetResult(result);
    }

    public void Cancel()
    {
        _completion.TrySetCanceled(CapturedToken);
    }
}

internal static class TestData
{
    public static FileUsageItem File(string rootPath, string relativePath, long sizeBytes, DateTime? modifiedUtc = null, DateTime? accessedUtc = null)
    {
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        return new FileUsageItem(
            Path.GetFileName(fullPath),
            fullPath,
            Path.GetDirectoryName(fullPath) ?? rootPath,
            sizeBytes,
            modifiedUtc,
            accessedUtc);
    }

    public static ScanResult ScanResultFromFiles(string rootPath, params FileUsageItem[] files)
    {
        var sorted = ScanViewBuilder.SortFiles(files).ToList();
        var root = ScanViewBuilder.BuildRootFromFiles(rootPath, sorted);
        return new ScanResult(root, sorted, sorted.Count, TimeSpan.FromSeconds(1.25));
    }
}