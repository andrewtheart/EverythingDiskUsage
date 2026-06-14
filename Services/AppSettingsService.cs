using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EverythingDiskUsage.Services;

public sealed class AppSettings
{
    public AppLogLevel MinimumLogLevel { get; set; } = AppLogLevel.Info;

    public bool LogEachSdkFile { get; set; }

    public bool LogToDebugOutput { get; set; }

    public int RetainedLogFiles { get; set; } = 20;

    public AppSettings Clone()
    {
        return new AppSettings
        {
            MinimumLogLevel = MinimumLogLevel,
            LogEachSdkFile = LogEachSdkFile,
            LogToDebugOutput = LogToDebugOutput,
            RetainedLogFiles = RetainedLogFiles
        };
    }
}

public static class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SettingsDirectory { get; } = ResolveSettingsDirectory();

    public static string SettingsFilePath { get; } = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        Directory.CreateDirectory(SettingsDirectory);

        if (!File.Exists(SettingsFilePath))
        {
            var defaults = CreateDefault();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? CreateDefault();
            return Normalize(settings);
        }
        catch
        {
            var defaults = CreateDefault();
            Save(defaults);
            return defaults;
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var normalized = Normalize(settings);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }

    public static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            MinimumLogLevel = AppLogLevel.Info,
            LogEachSdkFile = IsEnabled("EVERYTHING_DISK_USAGE_LOG_EACH_FILE"),
            LogToDebugOutput = false,
            RetainedLogFiles = 20
        };
    }

    public static AppSettings Normalize(AppSettings settings)
    {
        var normalized = settings.Clone();
        if (!Enum.IsDefined(normalized.MinimumLogLevel))
        {
            normalized.MinimumLogLevel = AppLogLevel.Info;
        }

        normalized.RetainedLogFiles = Math.Clamp(normalized.RetainedLogFiles, 1, 500);
        return normalized;
    }

    private static string ResolveSettingsDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "EverythingDiskUsage");
        }

        return Path.Combine(Path.GetTempPath(), "EverythingDiskUsage");
    }

    private static bool IsEnabled(string environmentVariableName)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariableName);
        return value is not null &&
            (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
