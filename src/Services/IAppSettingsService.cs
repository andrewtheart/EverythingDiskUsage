namespace EverythingDiskUsage.Services;

public interface IAppSettingsService
{
    string SettingsDirectory { get; }

    string SettingsFilePath { get; }

    AppSettings Load();

    void Save(AppSettings settings);

    AppSettings CreateDefault();

    AppSettings Normalize(AppSettings settings);
}
