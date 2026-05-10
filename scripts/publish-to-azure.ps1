<#
.SYNOPSIS
    Builds Everything Disk Usage, packages it as a self-contained ZIP, and
    publishes it to the Azure Blob static site at
    https://installmonitordl.z13.web.core.windows.net/

.DESCRIPTION
    1. dotnet publish (self-contained, win-x64)
    2. Generates a standalone Install-EverythingDiskUsage.ps1 inside the package
    3. Zips everything into EverythingDiskUsage-{version}.zip
    4. Uploads the ZIP to the private 'downloads' container
    5. Downloads the live index.html, surgically adds or replaces only the
       Everything Disk Usage card, and re-uploads it.
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$StorageAccount = "installmonitordl",
    [string]$ResourceGroup = "rg-installmonitor-download",
    [string]$StaticContainer = '$web',
    [string]$DownloadsContainer = 'downloads',
    [ValidateSet('login', 'key')]
    [string]$AuthMode = 'key'
)

$ErrorActionPreference = "Stop"

function Invoke-WithRetry {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Operation,
        [string]$Name,
        [int]$Attempts = 3
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        & $Operation
        if ($LASTEXITCODE -eq 0) { return }

        if ($attempt -eq $Attempts) {
            throw "$Name failed after $Attempts attempts"
        }

        Write-Warning "$Name failed on attempt $attempt of $Attempts; retrying..."
    }
}

$root = $PSScriptRoot | Split-Path -Parent
$projectPath = Join-Path $root "EverythingDiskUsage.csproj"
$projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw)

$appTitle = "Everything Disk Usage"
$packageName = "EverythingDiskUsage"
$exeName = "EverythingDiskUsage.exe"

$version = @(
    $projectXml.Project.PropertyGroup | ForEach-Object { $_.Version }
    $projectXml.Project.PropertyGroup | ForEach-Object { $_.AssemblyVersion }
    $projectXml.Project.PropertyGroup | ForEach-Object { $_.FileVersion }
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { $version = "1.0.0" }

Write-Host "Publishing $appTitle $version" -ForegroundColor Cyan

$storageAuthArgs = @('--auth-mode', 'login')
if ($AuthMode -eq 'key') {
    Write-Host "Retrieving storage account key for $StorageAccount..." -ForegroundColor Cyan
    $storageAccountKey = az storage account keys list `
        --resource-group $ResourceGroup `
        --account-name $StorageAccount `
        --query '[0].value' `
        -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($storageAccountKey)) {
        throw "Failed to retrieve storage account key for $StorageAccount"
    }
    $storageAuthArgs = @('--account-key', $storageAccountKey)
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("everything-disk-usage-release-" + [Guid]::NewGuid().ToString('N'))
$publishDir = Join-Path $tempRoot "publish"
$zipName = "$packageName-$version.zip"
$zipPath = Join-Path $tempRoot $zipName
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

try {
    Write-Host "`n=== Step 1: dotnet publish ===" -ForegroundColor Cyan
    dotnet publish $projectPath `
        -c $Configuration `
        -r win-x64 `
        --self-contained `
        -o $publishDir `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    $exePath = Join-Path $publishDir $exeName
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "Publish output did not contain $exeName"
    }

    Write-Host "`n=== Step 2: Generate Install-EverythingDiskUsage.ps1 ===" -ForegroundColor Cyan
    $installScript = @'
<#
.SYNOPSIS
    Installs Everything Disk Usage from the extracted package directory.

.EXAMPLE
    .\Install-EverythingDiskUsage.ps1
    .\Install-EverythingDiskUsage.ps1 -InstallDir "C:\Tools\EverythingDiskUsage"
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$Force,
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$sourceDir = $PSScriptRoot
$exeName = '__EXE_NAME__'
$displayName = '__DISPLAY_NAME__'
$regPath = 'HKCU:\Software\EverythingDiskUsage'
$regValName = 'InstallDir'

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = if (Test-Path $regPath) {
        try { (Get-ItemProperty $regPath -Name $regValName -ErrorAction Stop).$regValName } catch { $null }
    } else { $null }

    if ([string]::IsNullOrWhiteSpace($InstallDir)) {
        $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\EverythingDiskUsage'
    }
}

$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$exeFull = Join-Path $InstallDir $exeName

Get-Process -Name 'EverythingDiskUsage' -ErrorAction SilentlyContinue | Where-Object {
    try { [IO.Path]::GetFullPath($_.MainModule.FileName) -eq $exeFull } catch { $false }
} | ForEach-Object {
    Write-Warning "Stopping running $displayName (PID $($_.Id))..."
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(3000)) { $_.Kill() }
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Write-Host "Copying files to $InstallDir ..."
Get-ChildItem -LiteralPath $sourceDir -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $InstallDir -Recurse -Force
}

if (-not (Test-Path -LiteralPath $exeFull)) {
    throw "Install failed: $exeName not found in $InstallDir"
}

New-Item -Path $regPath -Force | Out-Null
Set-ItemProperty -Path $regPath -Name $regValName -Value $InstallDir
Set-ItemProperty -Path $regPath -Name 'DisplayName' -Value $displayName
Set-ItemProperty -Path $regPath -Name 'ExecutablePath' -Value $exeFull
Set-ItemProperty -Path $regPath -Name 'InstalledAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))

if (-not $NoShortcut) {
    $shortcutDir = [Environment]::GetFolderPath('Programs')
    $shortcutPath = Join-Path $shortcutDir "$displayName.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exeFull
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description = $displayName
    $shortcut.Save()
    Write-Host "Start Menu shortcut: $shortcutPath"
}

Write-Host "Installed to $InstallDir" -ForegroundColor Green
'@
    $installScript = $installScript.Replace('__EXE_NAME__', $exeName).Replace('__DISPLAY_NAME__', $appTitle)
    Set-Content -LiteralPath (Join-Path $publishDir "Install-EverythingDiskUsage.ps1") -Value $installScript -Encoding UTF8
    Write-Host "  Written Install-EverythingDiskUsage.ps1"

    Write-Host "`n=== Step 3: Create ZIP ===" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $zipSizeMB = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
    Write-Host "  $zipName - $zipSizeMB MB"

    Write-Host "`n=== Step 4: Upload ZIP (downloads container) ===" -ForegroundColor Cyan
    Invoke-WithRetry -Name "ZIP upload" -Operation {
        az storage blob upload `
            --account-name $StorageAccount `
            --container-name $DownloadsContainer `
            --name $zipName `
            --file $zipPath `
            --overwrite `
            --content-type "application/zip" `
            @storageAuthArgs `
            --max-connections 1 `
            -o none
    }
    Write-Host "  Uploaded $zipName to '$DownloadsContainer' container"

    Write-Host "`n=== Step 5: Add Everything Disk Usage card in index.html ===" -ForegroundColor Cyan

    $siteDir = "D:\installationSite"
    $htmlPath = Join-Path $siteDir "index.html"
    New-Item -ItemType Directory -Path $siteDir -Force | Out-Null

    Invoke-WithRetry -Name "index.html download" -Operation {
        az storage blob download `
            --account-name $StorageAccount `
            --container-name $StaticContainer `
            --name "index.html" `
            --file $htmlPath `
            --overwrite `
            @storageAuthArgs `
            --max-connections 1 `
            -o none
    }

    $live = Get-Content -LiteralPath $htmlPath -Raw -Encoding UTF8
    $beforeCardCount = ([regex]::Matches($live, '<div class="card">')).Count
    $beforeTitles = @([regex]::Matches($live, '(?s)<div class="card">\s*<h1>(.*?)</h1>') | ForEach-Object { $_.Groups[1].Value })

    $cst = [System.TimeZoneInfo]::FindSystemTimeZoneById('Central Standard Time')
    $nowCst = [System.TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::UtcNow, $cst)
    $releasedDate = $nowCst.ToString('MMM d, yyyy h:mm tt') + ' CST'
    $compiledCst = [System.TimeZoneInfo]::ConvertTimeFromUtc((Get-Item -LiteralPath $exePath).LastWriteTimeUtc, $cst)
    $compiledDate = $compiledCst.ToString('MMM d, yyyy h:mm tt') + ' CST'

    $newCard = @"
        <div class="card">
            <h1>$appTitle</h1>
            <p class="version">v$version &middot; Windows x64</p>
            <p class="desc">Visualize folder sizes from the Everything index with a tree, summaries, and pie chart for quick disk cleanup.</p>
            <a href="#" data-blob="$zipName" class="btn download-btn">Download ZIP</a>
            <p class="size">~$zipSizeMB MB &middot; Requires Windows 10/11 and Everything Search</p>
            <p class="note">Extract the ZIP and run <strong>Install-EverythingDiskUsage.ps1</strong>, or launch <strong>$exeName</strong> directly.<br>No .NET SDK or runtime required.</p>
            <p class="size" style="margin-top: 8px;">Compiled: $compiledDate &middot; Released: $releasedDate</p>
        </div>
"@

    $cardRegex = '(?s)[ \t]*<div class="card">\s*<h1>Everything Disk Usage</h1>.*?</div>'
    $replacedExisting = $false
    if ($live -match $cardRegex) {
        $updated = [regex]::Replace($live, $cardRegex, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $newCard }, 1)
        $replacedExisting = $true
        Write-Host "  Replaced existing Everything Disk Usage card."
    }
    elseif ($live -match '(?s)(\s*</div>\s*</div>\s*<script>)') {
        $updated = [regex]::Replace($live, '(?s)(\s*</div>\s*</div>\s*<script>)', ("`r`n" + $newCard + '$1'), 1)
        Write-Host "  Appended new Everything Disk Usage card."
    }
    else {
        throw "Could not locate a safe insertion point in live index.html. Refusing to overwrite the file."
    }

    foreach ($title in $beforeTitles | Where-Object { $_ -ne $appTitle }) {
        if (-not $updated.Contains("<h1>$title</h1>")) {
            throw "Safety check failed: existing card '$title' was not preserved. Refusing to upload index.html."
        }
    }

    $afterCardCount = ([regex]::Matches($updated, '<div class="card">')).Count
    $expectedCardCount = if ($replacedExisting) { $beforeCardCount } else { $beforeCardCount + 1 }
    if ($afterCardCount -ne $expectedCardCount) {
        throw "Safety check failed: expected $expectedCardCount cards after edit, found $afterCardCount. Refusing to upload index.html."
    }

    Set-Content -LiteralPath $htmlPath -Value $updated -Encoding UTF8 -NoNewline

    Invoke-WithRetry -Name "index.html upload" -Operation {
        az storage blob upload `
            --account-name $StorageAccount `
            --container-name $StaticContainer `
            --name "index.html" `
            --file $htmlPath `
            --overwrite `
            --content-type "text/html" `
            @storageAuthArgs `
            --max-connections 1 `
            -o none
    }
    Write-Host "  Uploaded index.html with $afterCardCount app cards"

    Write-Host ""
    Write-Host "=== Done! ===" -ForegroundColor Green
    Write-Host "Site: https://installmonitordl.z13.web.core.windows.net/" -ForegroundColor Yellow
    Write-Host "ZIP:  in '$DownloadsContainer' container as $zipName" -ForegroundColor Yellow
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}