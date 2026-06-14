; installer.iss — Everything Disk Usage Inno Setup script
;
; Built automatically by the MSBuild AfterPublish target in EverythingDiskUsage.csproj.
; You can also build manually:
;   ISCC.exe /DPublishDir="D:\path\to\publish" /DAppVersion="1.0.0" /DOutputDir="D:\path\to\output" installer.iss
;
; Prerequisite: Inno Setup 6 — https://jrsoftware.org/isdl.php

; ── Preprocessor defines (overridden by /D on the ISCC command line) ─────────
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #error PublishDir must be defined. Pass /DPublishDir="path\to\publish\output" to ISCC.
#endif
#ifndef OutputDir
  #define OutputDir "..\installer-output"
#endif

#define AppName     "Everything Disk Usage"
#define AppPublisher "EverythingDiskUsage"
#define AppExeName  "EverythingDiskUsage.exe"
#define EverythingURL "https://www.voidtools.com/downloads/"

; ── [Setup] ──────────────────────────────────────────────────────────────────
[Setup]
; Keep this GUID stable across versions so Windows recognises upgrades.
AppId={{8F4A2B1C-3D5E-4F6A-B7C8-9D0E1F2A3B4C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#EverythingURL}
AppSupportURL={#EverythingURL}
AppUpdatesURL={#EverythingURL}

; Per-user install — no UAC elevation required.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\EverythingDiskUsage
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; Architecture guard: this is a 64-bit-only app.
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os

; Offer to close the app if it is already running before overwriting files.
CloseApplications=yes

SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
AllowNoIcons=yes

OutputDir={#OutputDir}
OutputBaseFilename=EverythingDiskUsage-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

; ── [Languages] ──────────────────────────────────────────────────────────────
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; ── [Tasks] ──────────────────────────────────────────────────────────────────
[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; ── [Files] ──────────────────────────────────────────────────────────────────
[Files]
; Self-contained publish output — everything the app needs is in this folder.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; ── [Icons] ──────────────────────────────────────────────────────────────────
[Icons]
Name: "{group}\{#AppName}";   Filename: "{app}\{#AppExeName}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

; ── [Run] ────────────────────────────────────────────────────────────────────
[Run]
Filename: "{app}\{#AppExeName}"; \
  Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent

; ── [Code] ───────────────────────────────────────────────────────────────────
[Code]

// ── Everything Search detection ─────────────────────────────────────────────
// Everything Disk Usage communicates with the Everything Search engine via IPC
// (Everything64.dll ships with the app but needs the engine running).
// We probe registry keys and common executable locations; any match means
// Everything is installed. Do not rely only on default registry values, because
// some Everything installs create the key without a default string value.
function EverythingInstalled(): Boolean;
var
  S: String;
begin
  Result :=
    RegKeyExists(HKCU, 'Software\voidtools\Everything') or
    RegKeyExists(HKLM, 'Software\voidtools\Everything') or
    RegKeyExists(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Everything') or
    RegKeyExists(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Everything') or
    RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Everything') or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Everything.exe', '', S) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Everything.exe', '', S) or
    FileExists(ExpandConstant('{pf}\Everything\Everything.exe')) or
    FileExists(ExpandConstant('{pf32}\Everything\Everything.exe')) or
    FileExists(ExpandConstant('{localappdata}\Programs\Everything\Everything.exe'));
end;

// Warn the user about the missing prerequisite early in the wizard.
function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;  // Always allow installation to proceed.

  if not EverythingInstalled() then begin
    if MsgBox(
        'Everything Search (by voidtools) does not appear to be installed.'    + #13#10 + #13#10 +
        'Everything Disk Usage uses the Everything Search engine to build its' + #13#10 +
        'disk-usage index. The app will not function until Everything is'      + #13#10 +
        'installed and running.'                                                + #13#10 + #13#10 +
        'Would you like to open the Everything download page now?'             + #13#10 +
        '(You can install Everything later — click No to continue setup.)',
        mbInformation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', '{#EverythingURL}', '', '', SW_SHOW, ewNoWait, ErrorCode);
    end;
  end;
end;
