# Everything Disk Usage

A WPF disk usage viewer that queries voidtools Everything through the in-process SDK and builds a folder-size tree from indexed file paths and file-size metadata.

## Run

```powershell
cd D:\Everyything
dotnet run
```

Everything Search must be installed, running, and indexed. The app includes `Everything64.dll`, copied from the existing `D:\yagu` example.

## Logs

The app writes verbose logs to:

```text
%LOCALAPPDATA%\EverythingDiskUsage\logs\
```

Each run creates a timestamped `EverythingDiskUsage-*.log` file. The log includes app startup/shutdown, UI operations, scan progress, folder/file detail population, and detailed Everything SDK query setup, execution, result processing, errors, and cleanup.

Set `EVERYTHING_DISK_USAGE_LOG_EACH_FILE=1` before launching the app to log every accepted SDK file result. Without that switch, the scanner logs the first few files and periodic progress samples to avoid generating very large logs on full-drive scans.