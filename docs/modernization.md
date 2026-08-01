# Modernization

## .NET 11 Upgrade

Everything Disk Usage moved from .NET 10 to .NET 11 Preview 6 on August 1, 2026. The change shipped in version `1.0.2` at commit `3d8a052`.

### Runtime and SDK

- The application and test projects target `net11.0-windows`.
- `global.json` pins SDK `11.0.100-preview.6.26359.118`, allows prerelease SDKs, and permits roll-forward to a newer .NET 11 feature band.
- `Microsoft.Extensions.DependencyInjection` is aligned to `11.0.0-preview.6.26359.118`.
- Build output now resides under `net11.0-windows` directories.

The checked-in installer remains self-contained. End users do not need to install the .NET 11 runtime; the SDK is required only for source builds.

### Changed Files

- `src/EverythingDiskUsage.csproj`
- `tests/EverythingDiskUsage.Tests/EverythingDiskUsage.Tests.csproj`
- `global.json`
- `README.md`

No application behavior or persisted settings format changed as part of the framework upgrade.

### Validation

The migration was verified with SDK `11.0.100-preview.6.26359.118` on Windows:

```powershell
dotnet restore .\EverythingDiskUsage.slnx
dotnet test .\EverythingDiskUsage.slnx -c Debug --no-restore
dotnet build .\EverythingDiskUsage.slnx -c Release --no-restore
```

All 51 automated tests passed. The Release build completed successfully, and the application started under `.NET 11.0.0-preview.6.26359.118` with both `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App` resolved to the matching runtime.

### Preview Lifecycle

.NET 11 was still a preview at the time of this migration. When a newer preview, release candidate, or stable SDK is adopted:

1. Update the SDK version in `global.json`.
2. Align `Microsoft.Extensions.DependencyInjection` to the corresponding release.
3. Restore, run the full test suite, and build Release.
4. Rebuild the self-contained installer before publishing the next release.
