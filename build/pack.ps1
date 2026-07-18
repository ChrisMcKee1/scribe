#requires -Version 7.0
<#
.SYNOPSIS
    Builds a GitHub-release-backed Velopack installer and delta updates for Scribe.

.DESCRIPTION
    Scribe self-updates at runtime from its GitHub Releases (see Infrastructure/UpdateService.cs).
    This script produces the matching release artifacts:

        1. dotnet publish  -> a self-contained win-x64 build (no .NET install required on the user PC).
        2. vpk pack        -> Setup.exe (installer), a full .nupkg, delta packages, and RELEASES.
        3. vpk upload       -> attaches those artifacts to a GitHub Release (optional, -Publish).

.LINK
    https://docs.velopack.io/               (Velopack)
    https://docs.velopack.io/integrating/cli (vpk command reference)

.EXAMPLE
    ./build/pack.ps1
#>
[CmdletBinding()]
param(
    # Semantic version for this release. Keep in sync with Directory.Build.props (<VersionPrefix>).
    [string]$Version,

    # Build configuration.
    [string]$Configuration = 'Release',

    # owner/repo the installer pulls updates from. Must match RepositoryUrl in Directory.Build.props.
    [string]$GitHubRepo = 'ChrisMcKee1/scribe',

    # When set, uploads the produced artifacts to a GitHub Release (needs $env:GITHUB_TOKEN).
    [switch]$Publish,

    # Run only version/model preflight. Does not publish, install tools, or delete build output.
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProj  = Join-Path $repoRoot 'src/Scribe.App/Scribe.App.csproj'
$publishDir = Join-Path $repoRoot 'publish/win-x64'
$releaseDir = Join-Path $repoRoot 'releases'
$packId = 'Scribe'
$mainExe = 'Scribe.exe'
$runtime = 'win-x64'

$propsPath = Join-Path $repoRoot 'Directory.Build.props'
[xml]$props = Get-Content $propsPath
$sourceVersion = [string]$props.Project.PropertyGroup.VersionPrefix
if ([string]::IsNullOrWhiteSpace($sourceVersion)) { throw "VersionPrefix missing from $propsPath" }
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $sourceVersion }
if ($Version -ne $sourceVersion) {
    throw "Requested version $Version does not match Directory.Build.props version $sourceVersion. Update VersionPrefix first."
}

. (Join-Path $repoRoot 'scripts/Model-Manifest.ps1')
$sourceModels = Join-Path $repoRoot 'src/Scribe.App/models'
Test-ScribeRuntimeModels -ModelsDir $sourceModels -VerifyHashes
Write-Host "==> Runtime model preflight passed ($($ScribeRuntimeModelManifest.Count) files)." -ForegroundColor Green

if ($ValidateOnly) {
    Write-Host "==> Release preflight passed for Scribe $Version." -ForegroundColor Green
    return
}

Write-Host "==> Scribe pack  v$Version  ($Configuration, $runtime)" -ForegroundColor Cyan

# --- 0. Ensure the Velopack CLI (vpk) is available -------------------------------------------------
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host '==> Installing Velopack CLI (vpk) as a global tool...' -ForegroundColor Yellow
    dotnet tool install -g vpk
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}

# --- 1. Publish a self-contained win-x64 build -----------------------------------------------------
Write-Host '==> dotnet publish (self-contained)...' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $appProj `
    -c $Configuration `
    -r $runtime `
    --self-contained true `
    -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# --- 1b. Publish the WinUI 3 overlay into the app payload under Overlay\ ----------------------------
# The installed app resolves the recording pill at <BaseDirectory>\Overlay\Scribe.Overlay.exe
# (OverlayProcessClient.ResolveOverlayExe, strategy 2). It must be self-contained + unpackaged so it
# starts with no machine-wide Windows App SDK runtime. Published AFTER the app publish above because
# that step wipes and recreates $publishDir.
Write-Host '==> dotnet publish overlay (self-contained WinUI 3)...' -ForegroundColor Cyan
$overlayProj = Join-Path $repoRoot 'src/Scribe.Overlay/Scribe.Overlay.csproj'
$overlayDir  = Join-Path $publishDir 'Overlay'
dotnet publish $overlayProj `
    -c $Configuration `
    -r $runtime `
    --self-contained true `
    -p:Platform=x64 `
    -p:Version=$Version `
    -o $overlayDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (overlay) failed.' }
$overlayExe = Join-Path $overlayDir 'Scribe.Overlay.exe'
if (-not (Test-Path $overlayExe)) { throw "Overlay exe missing after publish: $overlayExe" }
Write-Host "==> Overlay bundled at: $overlayExe" -ForegroundColor Green

$publishedModels = Join-Path $publishDir 'models'
Test-ScribeRuntimeModels -ModelsDir $publishedModels -VerifyHashes
Write-Host '==> Published runtime model payload verified.' -ForegroundColor Green

# --- 2. Pack with Velopack -------------------------------------------------------------------------
$currentFullName = "Scribe-$Version-win-x64-full.nupkg"
$priorFullPackages = if (Test-Path $releaseDir) {
    @(Get-ChildItem $releaseDir -Filter "Scribe-*-$runtime-full.nupkg" -File |
        Where-Object { $_.Name -ne $currentFullName })
}
else {
    @()
}

$packArgs = @(
    'pack',
    '--packId', $packId,
    '--packVersion', $Version,
    '--packDir', $publishDir,
    '--mainExe', $mainExe,
    '--outputDir', $releaseDir,
    '--channel', $runtime
)

Write-Host '==> vpk pack...' -ForegroundColor Cyan
vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }

$expectedArtifacts = @(
    (Join-Path $releaseDir 'releases.win-x64.json'),
    (Join-Path $releaseDir $currentFullName),
    (Join-Path $releaseDir 'Scribe-win-x64-Portable.zip'),
    (Join-Path $releaseDir 'Scribe-win-x64-Setup.exe')
)
if ($priorFullPackages.Count -gt 0) {
    $expectedArtifacts += Join-Path $releaseDir "Scribe-$Version-win-x64-delta.nupkg"
}
foreach ($artifact in $expectedArtifacts) {
    if (-not (Test-Path $artifact -PathType Leaf)) {
        throw "Expected release artifact missing: $artifact"
    }
}

Write-Host "==> Artifacts written to: $releaseDir" -ForegroundColor Green
Get-ChildItem $releaseDir | Select-Object Name, Length | Format-Table -AutoSize

# --- 3. Optionally publish to a GitHub Release -----------------------------------------------------
if ($Publish) {
    if (-not $env:GITHUB_TOKEN) { throw 'Set $env:GITHUB_TOKEN (repo scope) before using -Publish.' }
    Write-Host "==> Uploading release to github.com/$GitHubRepo ..." -ForegroundColor Cyan
    vpk upload github `
        --repoUrl "https://github.com/$GitHubRepo" `
        --publish `
        --releaseName "Scribe $Version" `
        --tag "v$Version" `
        --token $env:GITHUB_TOKEN `
        --outputDir $releaseDir `
        --channel $runtime
    if ($LASTEXITCODE -ne 0) { throw 'vpk upload failed.' }
    Write-Host '==> Published.' -ForegroundColor Green
}
else {
    Write-Host '==> Skipped GitHub upload (pass -Publish to upload).' -ForegroundColor Yellow
}
