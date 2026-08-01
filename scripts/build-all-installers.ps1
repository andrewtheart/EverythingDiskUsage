<#
.SYNOPSIS
    Builds the Everything Disk Usage installer and can publish a GitHub release.

.DESCRIPTION
    Publishes the self-contained win-x64 app, builds the Inno Setup installer,
    and optionally commits, pushes, and creates or refreshes a GitHub release.

    The installer is written to installer-output. Build failures prevent commit,
    push, and release operations.

.PARAMETER Version
    Release version without a leading "v". When omitted with Push, increments
    the patch number of the newest stable local tag, origin tag, or GitHub
    release (including drafts). Otherwise uses the newest stable local tag, or
    1.0.0 when no version tag exists.

.PARAMETER Configuration
    .NET build configuration. Defaults to Release.

.PARAMETER IsccPath
    Optional path to ISCC.exe when Inno Setup 6 is not installed in a standard
    machine-wide location.

.PARAMETER Commit
    After the installer succeeds, stages all pending repository changes and
    commits them as "Build installer v<version> (x64)". Does nothing when the
    working tree is clean.

.PARAMETER Push
    Implies Commit, pushes the current branch, and prompts whether the GitHub
    release should remain a draft or be published unless SkipRelease is used.
    When Version is omitted, automatically increments the patch version. An
    explicit existing release version refreshes its installer asset.

.PARAMETER ReleaseMode
    GitHub release mode used with Push: Prompt (the default), Draft, or
    Published. Draft and Published are useful for unattended runs.

.PARAMETER SkipRelease
    With Push, skips GitHub release creation or asset refresh.

.EXAMPLE
    .\build-all-installers.ps1
    Builds the x64 installer using the newest local version tag.

.EXAMPLE
    .\build-all-installers.ps1 -Version 1.1.0
    Builds the x64 installer for version 1.1.0.

.EXAMPLE
    .\build-all-installers.ps1 -Version 1.1.0 -WhatIf
    Prints the build and release plan without changing files.

.EXAMPLE
    .\build-all-installers.ps1 -Push
    Increments the latest patch version, builds and commits the installer,
    pushes the current branch, then prompts for a draft or published release.

.EXAMPLE
    .\build-all-installers.ps1 -Push -ReleaseMode Published
    Builds, pushes, and publishes the next patch release without prompting.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [string]$IsccPath,
    [switch]$Commit,
    [switch]$Push,
    [switch]$SkipRelease,
    [ValidateSet('Prompt', 'Draft', 'Published')]
    [string]$ReleaseMode = 'Prompt'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\EverythingDiskUsage.csproj'
$publishDir = Join-Path $repoRoot 'artifacts\publish'
$installerOutput = Join-Path $repoRoot 'installer-output'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project not found: $projectPath"
}

function Resolve-ReleaseMode {
    if ($ReleaseMode -ne 'Prompt') { return $ReleaseMode }

    while ($true) {
        Write-Host ''
        Write-Host 'How should the GitHub release be created?' -ForegroundColor Cyan
        Write-Host '  [D] Draft - upload it for review without publishing'
        Write-Host '  [P] Publish - publish it officially and mark it latest'
        $choice = (Read-Host 'Choose D or P').Trim().ToLowerInvariant()
        switch ($choice) {
            { $_ -in @('d', 'draft') } { return 'Draft' }
            { $_ -in @('p', 'publish', 'published') } { return 'Published' }
            default { Write-Warning "Invalid choice '$choice'. Enter D for Draft or P for Publish." }
        }
    }
}

function Get-RepositorySlug {
    $originUrl = "$(& git -C $repoRoot remote get-url origin 2>$null)"
    if ($LASTEXITCODE -eq 0 -and $originUrl -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$') {
        return "$($Matches.owner)/$($Matches.repo)"
    }

    return $null
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $versionTags = New-Object System.Collections.Generic.List[string]
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $localTags = @(& git -C $repoRoot tag --list 'v[0-9]*' 2>$null)
        if ($LASTEXITCODE -eq 0) {
            foreach ($tag in $localTags) { $versionTags.Add("$tag") }
        }

        if ($Push) {
            $remoteTags = @(& git -C $repoRoot ls-remote --tags origin 'refs/tags/v*' 2>$null)
            if ($LASTEXITCODE -eq 0) {
                foreach ($tag in $remoteTags) { $versionTags.Add("$tag") }
            }
            else {
                Write-Warning 'Could not read release tags from origin; auto-incrementing from local tags only.'
            }

            $gh = Get-Command gh -ErrorAction SilentlyContinue
            $repoSlug = Get-RepositorySlug
            if ($gh -and $repoSlug) {
                $releaseJson = "$(& $gh.Source release list --limit 100 --json tagName --repo $repoSlug 2>$null)"
                if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($releaseJson)) {
                    try {
                        foreach ($release in @($releaseJson | ConvertFrom-Json)) {
                            $versionTags.Add("$($release.tagName)")
                        }
                    }
                    catch {
                        Write-Warning 'Could not parse GitHub release versions; continuing with Git tags only.'
                    }
                }
            }
        }
    }

    $stableVersions = @(
        foreach ($tag in $versionTags) {
            if ($tag -match '(?:^|refs/tags/)v(?<version>\d+\.\d+\.\d+)(?:\^\{\})?$') {
                [version]$Matches.version
            }
        }
    )
    $latestVersion = $stableVersions | Sort-Object -Descending | Select-Object -First 1

    if ($Push -and $null -ne $latestVersion) {
        $Version = '{0}.{1}.{2}' -f $latestVersion.Major, $latestVersion.Minor, ($latestVersion.Build + 1)
    }
    elseif ($null -ne $latestVersion) {
        $Version = $latestVersion.ToString(3)
    }
    else {
        $Version = '1.0.0'
    }
}
else {
    $Version = $Version.Trim() -replace '^v', ''
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Invalid version '$Version'. Use a semantic version such as 1.1.0 (without a leading v)."
}

$installerPath = Join-Path $installerOutput "EverythingDiskUsage-Setup-$Version.exe"
$publishArguments = @(
    'publish',
    $projectPath,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained',
    '-o', $publishDir,
    '--nologo',
    "-p:Version=$Version"
)
if (-not [string]::IsNullOrWhiteSpace($IsccPath)) {
    $publishArguments += "-p:IsccPath=$IsccPath"
}

Write-Host "Everything Disk Usage installer build - version $Version - platform: x64" -ForegroundColor Cyan

if ($WhatIfPreference) {
    Write-Host 'WhatIf: the following build would run:' -ForegroundColor Yellow
    Write-Host "  dotnet publish src\EverythingDiskUsage.csproj -c $Configuration -r win-x64 --self-contained -o artifacts\publish -p:Version=$Version"
    Write-Host "  expected installer: installer-output\EverythingDiskUsage-Setup-$Version.exe"
    if ($Commit -or $Push) {
        Write-Host ("  post-build: git add -A + commit{0}" -f $(if ($Push) { ' + push' } else { '' })) -ForegroundColor Yellow
    }
    if ($Push -and -not $SkipRelease) {
        $plannedMode = if ($ReleaseMode -eq 'Prompt') { 'prompt for Draft or Published' } else { $ReleaseMode }
        Write-Host "  post-push: create or refresh GitHub release v$Version ($plannedMode) with the x64 installer" -ForegroundColor Yellow
    }
    return
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet is not available on PATH.'
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null

if (Test-Path -LiteralPath $installerPath) {
    Remove-Item -LiteralPath $installerPath -Force
}

Write-Host ''
Write-Host '############################################################' -ForegroundColor Cyan
Write-Host '# Building x64 installer (win-x64)' -ForegroundColor Cyan
Write-Host '############################################################' -ForegroundColor Cyan

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE)."
}
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Expected installer was not produced: $installerPath. Confirm Inno Setup 6 is installed or pass -IsccPath."
}

$installerFile = Get-Item -LiteralPath $installerPath
Write-Host ''
Write-Host '==================== Build summary ====================' -ForegroundColor Cyan
Write-Host ("  [OK] x64 -> {0} ({1} MB)" -f $installerFile.Name, [math]::Round($installerFile.Length / 1MB, 1)) -ForegroundColor Green
Write-Host '=======================================================' -ForegroundColor Cyan

if ($Commit -or $Push) {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw '-Commit/-Push was requested but git is not available on PATH.'
    }

    $restoreNativePreference = $false
    $savedNativePreference = $null
    if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
        $savedNativePreference = $PSNativeCommandUseErrorActionPreference
        $PSNativeCommandUseErrorActionPreference = $false
        $restoreNativePreference = $true
    }

    try {
        $inside = (& git -C $repoRoot rev-parse --is-inside-work-tree 2>$null)
        if ($LASTEXITCODE -ne 0 -or "$inside".Trim() -ne 'true') {
            throw "Repository root is not a Git working tree: $repoRoot"
        }

        Write-Host ''
        Write-Host 'Staging all changes (git add -A)...' -ForegroundColor Cyan
        & git -C $repoRoot add -A
        if ($LASTEXITCODE -ne 0) { throw "git add -A failed (exit $LASTEXITCODE)." }

        & git -C $repoRoot diff --cached --quiet
        $stagedExit = $LASTEXITCODE
        if ($stagedExit -gt 1) { throw "git diff --cached failed (exit $stagedExit)." }

        if ($stagedExit -eq 1) {
            $commitMessage = "Build installer v$Version (x64)"
            Write-Host "Committing: $commitMessage" -ForegroundColor Cyan
            & git -C $repoRoot commit -m $commitMessage
            if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)." }
            Write-Host 'Committed.' -ForegroundColor Green
        }
        else {
            Write-Host 'Nothing to commit - working tree already clean.' -ForegroundColor DarkGray
        }

        if ($Push) {
            $branch = ("$(& git -C $repoRoot rev-parse --abbrev-ref HEAD)").Trim()
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch) -or $branch -eq 'HEAD') {
                throw 'Cannot push from a detached HEAD or resolve the current branch.'
            }

            Write-Host "Pushing '$branch' to origin..." -ForegroundColor Cyan
            & git -C $repoRoot push origin $branch
            if ($LASTEXITCODE -ne 0) { throw "git push failed for branch '$branch' (exit $LASTEXITCODE)." }
            Write-Host 'Pushed.' -ForegroundColor Green

            if (-not $SkipRelease) {
                $gh = Get-Command gh -ErrorAction SilentlyContinue
                if (-not $gh) {
                    Write-Warning "GitHub release skipped: gh CLI was not found. Create it manually with: gh release create v$Version `"$installerPath`" --draft --generate-notes"
                }
                else {
                    $tag = "v$Version"
                    $resolvedReleaseMode = Resolve-ReleaseMode
                    $repoSlug = Get-RepositorySlug
                    $repoArguments = @()
                    if ($repoSlug) { $repoArguments = @('--repo', $repoSlug) }
                    $headSha = ("$(& git -C $repoRoot rev-parse HEAD)").Trim()

                    & $gh.Source release view $tag @repoArguments *> $null
                    if ($LASTEXITCODE -eq 0) {
                        Write-Host "GitHub release $tag already exists - refreshing installer asset..." -ForegroundColor Cyan
                        & $gh.Source release upload $tag $installerPath --clobber @repoArguments
                        if ($LASTEXITCODE -ne 0) {
                            throw "Release asset upload failed (exit $LASTEXITCODE)."
                        }

                        if ($resolvedReleaseMode -eq 'Draft') {
                            & $gh.Source release edit $tag --draft @repoArguments
                        }
                        else {
                            & $gh.Source release edit $tag --draft=false --latest @repoArguments
                        }
                        if ($LASTEXITCODE -ne 0) {
                            throw "Applying $resolvedReleaseMode mode to release $tag failed (exit $LASTEXITCODE)."
                        }
                    }
                    else {
                        Write-Host "Creating $($resolvedReleaseMode.ToLowerInvariant()) GitHub release $tag..." -ForegroundColor Cyan
                        $createArguments = @(
                            'release', 'create', $tag,
                            '--title', "Everything Disk Usage $Version",
                            '--generate-notes',
                            '--target', $headSha
                        )
                        if ($resolvedReleaseMode -eq 'Draft') { $createArguments += '--draft' }
                        else { $createArguments += '--latest' }
                        $createArguments += $installerPath
                        $createArguments += $repoArguments
                        & $gh.Source @createArguments
                        if ($LASTEXITCODE -ne 0) {
                            throw "Release creation failed (exit $LASTEXITCODE)."
                        }
                    }

                    $releaseUrl = if ($repoSlug) { "https://github.com/$repoSlug/releases" } else { 'the GitHub Releases page' }
                    if ($resolvedReleaseMode -eq 'Draft') {
                        Write-Host "Draft release $tag is ready for review at $releaseUrl" -ForegroundColor Green
                    }
                    else {
                        Write-Host "Release $tag is published as latest: $releaseUrl" -ForegroundColor Green
                    }
                }
            }
        }
    }
    finally {
        if ($restoreNativePreference) {
            $PSNativeCommandUseErrorActionPreference = $savedNativePreference
        }
    }
}