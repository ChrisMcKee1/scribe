#requires -Version 7.0
<#
.SYNOPSIS
    Builds a GitHub-release-backed Velopack installer and delta updates for Scribe.

.DESCRIPTION
    Scribe self-updates at runtime from its GitHub Releases (see Infrastructure/UpdateService.cs).
    This script produces the matching release artifacts:

        1. dotnet publish  -> a self-contained build (no .NET install required on the user PC).
        2. vpk pack        -> Setup.exe (installer), a full .nupkg, delta packages, and RELEASES.
        3. vpk upload       -> attaches those artifacts to a GitHub Release (optional, -Publish).

    Each architecture is packed into its own Velopack channel (win-x64, win-arm64), so an installed
    app only ever receives updates built for the silicon it is running on.

.LINK
    https://docs.velopack.io/               (Velopack)
    https://docs.velopack.io/integrating/cli (vpk command reference)

.EXAMPLE
    ./build/pack.ps1

.EXAMPLE
    ./build/pack.ps1 -Architecture arm64

.EXAMPLE
    ./build/pack.ps1 -Architecture all -Publish
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
    [switch]$ValidateOnly,

    # Target architecture. "all" builds x64 and arm64 in sequence, each into its own channel.
    [ValidateSet('x64', 'arm64', 'all')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProj  = Join-Path $repoRoot 'src/Scribe.App/Scribe.App.csproj'
$releaseDir = Join-Path $repoRoot 'releases'
$packId = 'Scribe'
$mainExe = 'Scribe.exe'

# Velopack channel names double as the RID. Keeping them identical means the channel an installed
# app polls for updates is literally the architecture it was built for, so an Arm64 install can
# never be handed an x64 delta.
# @(...) wraps the switch because a switch unrolls a single-element array back to a bare Hashtable,
# whose .Count reports its KEY count (2), not one target.
$targets = @(switch ($Architecture) {
    'x64'   { , @{ Runtime = 'win-x64';   OverlayPlatform = 'x64' } }
    'arm64' { , @{ Runtime = 'win-arm64'; OverlayPlatform = 'ARM64' } }
    'all'   { @(
                @{ Runtime = 'win-x64';   OverlayPlatform = 'x64' },
                @{ Runtime = 'win-arm64'; OverlayPlatform = 'ARM64' }
              ) }
})

$propsPath = Join-Path $repoRoot 'Directory.Build.props'
[xml]$props = Get-Content $propsPath
$sourceVersion = [string]$props.Project.PropertyGroup.VersionPrefix
if ([string]::IsNullOrWhiteSpace($sourceVersion)) { throw "VersionPrefix missing from $propsPath" }
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $sourceVersion }
if ($Version -ne $sourceVersion) {
    throw "Requested version $Version does not match Directory.Build.props version $sourceVersion. Update VersionPrefix first."
}

# Branding for the installer and the Add/Remove Programs entry, read from the same single source of
# truth as the version so the shipped metadata can never drift from the project file.
$packTitle = [string]$props.Project.PropertyGroup.Product
if ([string]::IsNullOrWhiteSpace($packTitle)) { $packTitle = $packId }
$packAuthors = [string]$props.Project.PropertyGroup.Authors
if ([string]::IsNullOrWhiteSpace($packAuthors)) { throw "Authors missing from $propsPath" }
$brandIcon = Join-Path $repoRoot 'src/Scribe.App/Assets/scribe.ico'
if (-not (Test-Path $brandIcon -PathType Leaf)) { throw "Brand icon missing: $brandIcon" }

. (Join-Path $repoRoot 'scripts/Model-Manifest.ps1')
. (Join-Path $repoRoot 'scripts/Payload-Architecture.ps1')
$sourceModels = Join-Path $repoRoot 'src/Scribe.App/models'
Test-ScribeRuntimeModels -ModelsDir $sourceModels -VerifyHashes
Write-Host "==> Runtime model preflight passed ($($ScribeRuntimeModelManifest.Count) files)." -ForegroundColor Green

if ($ValidateOnly) {
    Write-Host "==> Release preflight passed for Scribe $Version." -ForegroundColor Green
    return
}

Write-Host "==> Scribe $Version  ($Configuration)  architectures: $(($targets.Runtime) -join ', ')" -ForegroundColor Cyan

# --- 0. Ensure the Velopack CLI (vpk) is available -------------------------------------------------
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host '==> Installing Velopack CLI (vpk) as a global tool...' -ForegroundColor Yellow
    dotnet tool install -g vpk
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}

# --- 1. Build one architecture end to end ----------------------------------------------------------
# Publishes the app, publishes the matching overlay into its payload, verifies the model files
# survived, then packs a Velopack release into the channel named after that architecture.
function Invoke-ScribeArchitecture {
    param(
        [Parameter(Mandatory)][string]$Runtime,
        [Parameter(Mandatory)][string]$OverlayPlatform
    )

    $publishDir = Join-Path $repoRoot "publish/$Runtime"

    Write-Host ''
    Write-Host "==> Scribe pack  v$Version  ($Configuration, $Runtime)" -ForegroundColor Cyan

    Write-Host "==> dotnet publish (self-contained, $Runtime)..." -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish $appProj `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:Version=$Version `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Runtime." }

    # The installed app resolves the recording pill at <BaseDirectory>\Overlay\Scribe.Overlay.exe
    # (OverlayProcessClient.ResolveOverlayExe, strategy 2). It must be self-contained + unpackaged so
    # it starts with no machine-wide Windows App SDK runtime, and it must match the app's
    # architecture: the pill is a separate process, so an x64 pill beside an Arm64 app would run
    # emulated at best. Published AFTER the app publish because that step wipes $publishDir.
    Write-Host "==> dotnet publish overlay (self-contained WinUI 3, $OverlayPlatform)..." -ForegroundColor Cyan
    $overlayProj = Join-Path $repoRoot 'src/Scribe.Overlay/Scribe.Overlay.csproj'
    $overlayDir  = Join-Path $publishDir 'Overlay'
    dotnet publish $overlayProj `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:Platform=$OverlayPlatform `
        -p:Version=$Version `
        -o $overlayDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (overlay) failed for $Runtime." }
    $overlayExe = Join-Path $overlayDir 'Scribe.Overlay.exe'
    if (-not (Test-Path $overlayExe)) { throw "Overlay exe missing after publish: $overlayExe" }

    # An architecture mismatch here ships a silently-emulated pill, which looks like a performance
    # bug rather than a packaging bug, so assert it at pack time instead of discovering it in the wild.
    Test-ScribePayloadArchitecture -PublishDir $publishDir -Runtime $Runtime
    Write-Host "==> Overlay bundled at: $overlayExe" -ForegroundColor Green

    $publishedModels = Join-Path $publishDir 'models'
    Test-ScribeRuntimeModels -ModelsDir $publishedModels -VerifyHashes
    Write-Host '==> Published runtime model payload verified.' -ForegroundColor Green

    # --- 2. Pack with Velopack ---------------------------------------------------------------------
    $currentFullName = "Scribe-$Version-$Runtime-full.nupkg"
    $priorFullPackages = if (Test-Path $releaseDir) {
        @(Get-ChildItem $releaseDir -Filter "Scribe-*-$Runtime-full.nupkg" -File |
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
        '--channel', $Runtime,
        # Brand the installer and the Add/Remove Programs entry. Without an explicit icon vpk ships a
        # generic Setup.exe, and the publisher falls back to the pack id instead of the real author.
        '--icon', $brandIcon,
        '--packTitle', $packTitle,
        '--packAuthors', $packAuthors
    )

    Write-Host "==> vpk pack ($Runtime)..." -ForegroundColor Cyan
    vpk @packArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed for $Runtime." }

    $expectedArtifacts = @(
        (Join-Path $releaseDir "releases.$Runtime.json"),
        (Join-Path $releaseDir $currentFullName),
        (Join-Path $releaseDir "Scribe-$Runtime-Portable.zip"),
        (Join-Path $releaseDir "Scribe-$Runtime-Setup.exe")
    )
    if ($priorFullPackages.Count -gt 0) {
        $expectedArtifacts += Join-Path $releaseDir "Scribe-$Version-$Runtime-delta.nupkg"
    }
    foreach ($artifact in $expectedArtifacts) {
        if (-not (Test-Path $artifact -PathType Leaf)) {
            throw "Expected release artifact missing: $artifact"
        }
    }
}

foreach ($target in $targets) {
    Invoke-ScribeArchitecture -Runtime $target.Runtime -OverlayPlatform $target.OverlayPlatform
}

Write-Host "==> Artifacts written to: $releaseDir" -ForegroundColor Green
Get-ChildItem $releaseDir | Select-Object Name, Length | Format-Table -AutoSize

# --- 3. Optionally publish to a GitHub Release -----------------------------------------------------
# Each architecture is uploaded as its own channel against the same tag, so one GitHub Release
# carries both installers and each installed app only sees updates for its own silicon.
if ($Publish) {
    if (-not $env:GITHUB_TOKEN) { throw 'Set $env:GITHUB_TOKEN (repo scope) before using -Publish.' }
    foreach ($target in $targets) {
        Write-Host "==> Uploading $($target.Runtime) to github.com/$GitHubRepo ..." -ForegroundColor Cyan
        vpk upload github `
            --repoUrl "https://github.com/$GitHubRepo" `
            --publish `
            --releaseName "Scribe $Version" `
            --tag "v$Version" `
            --token $env:GITHUB_TOKEN `
            --outputDir $releaseDir `
            --channel $target.Runtime `
            --merge
        if ($LASTEXITCODE -ne 0) { throw "vpk upload failed for $($target.Runtime)." }
    }
    Write-Host '==> Published.' -ForegroundColor Green
}
else {
    Write-Host '==> Skipped GitHub upload (pass -Publish to upload).' -ForegroundColor Yellow
}
