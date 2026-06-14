# Everything Disk Usage

A WPF disk usage viewer that queries voidtools Everything through the in-process SDK and builds a folder-size tree from indexed file paths and file-size metadata.

## Prerequisites

- Windows 10/11 x64
- .NET 10 SDK for building from source
- Everything Search installed, running, and indexed
- Inno Setup 6, optional but recommended, to build the installer during `dotnet publish`

Install Inno Setup machine-wide with:

```powershell
winget install JRSoftware.InnoSetup --scope machine
```

## Run

```powershell
cd D:\EverythingDiskUsage
dotnet run --project .\EverythingDiskUsage.csproj
```

Everything Search must be installed, running, and indexed. The app includes `Everything64.dll`, copied from the existing `D:\yagu` example.

## Build

Build a Debug binary:

```powershell
cd D:\EverythingDiskUsage
dotnet build .\EverythingDiskUsage.csproj -c Debug
```

Build a Release binary:

```powershell
dotnet build .\EverythingDiskUsage.csproj -c Release
```

The normal build output is written under:

```text
bin\<Configuration>\net10.0-windows\
```

## Publish

Create a self-contained Windows x64 publish output:

```powershell
dotnet publish .\EverythingDiskUsage.csproj -c Release -r win-x64 --self-contained -o .\artifacts\publish
```

The app is published self-contained, so users do not need to install a separate .NET runtime. They still need Everything Search installed and running.

If Inno Setup 6 is installed machine-wide, `dotnet publish` also builds an installer automatically. The installer is written to:

```text
installer-output\EverythingDiskUsage-Setup-<version>.exe
```

If `ISCC.exe` is installed somewhere non-standard, pass its path explicitly:

```powershell
dotnet publish .\EverythingDiskUsage.csproj -c Release -r win-x64 --self-contained -o .\artifacts\publish /p:IsccPath="C:\Path\To\ISCC.exe"
```

## Release To Download Site

The Azure publishing script builds the app, creates the release ZIP, uploads it to the private downloads container, and updates the Everything Disk Usage card on the static download site:

```powershell
.\scripts\publish-to-azure.ps1 -Configuration Release
```

This requires Azure CLI access to the `installmonitordl` storage account in the `rg-installmonitor-download` resource group.

## Logs

The app writes verbose logs to:

```text
%LOCALAPPDATA%\EverythingDiskUsage\logs\
```

Each run creates a timestamped `EverythingDiskUsage-*.log` file. The log includes app startup/shutdown, UI operations, scan progress, folder/file detail population, and detailed Everything SDK query setup, execution, result processing, errors, and cleanup.

Set `EVERYTHING_DISK_USAGE_LOG_EACH_FILE=1` before launching the app to log every accepted SDK file result. Without that switch, the scanner logs the first few files and periodic progress samples to avoid generating very large logs on full-drive scans.

## License

Everything Disk Usage is released under the 0BSD license. See `LICENSE`.