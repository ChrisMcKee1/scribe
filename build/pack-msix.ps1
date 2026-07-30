#requires -Version 7.0
<#
.SYNOPSIS
    Builds an unsigned MSIX package of Scribe for Microsoft Store submission.

.DESCRIPTION
    The Store accepts either an MSIX or an existing .exe/.msi installer. MSIX is the path worth
    taking: Microsoft signs and hosts it at no cost, which removes the SmartScreen friction our
    unsigned Velopack installer carries, and it is the only option that supports S Mode and the
    Windows 11 backup and restore experience.

    This script produces the package only. Store submissions are signed by Microsoft after upload,
    so no certificate is needed here and none is ever read. For local side-load testing you must
    sign the output yourself with a certificate your machine trusts.

    The Velopack path in build/pack.ps1 is unaffected and remains the channel for direct downloads.

.LINK
    https://learn.microsoft.com/windows/apps/distribute-through-store/how-to-distribute-your-win32-app-through-microsoft-store
    https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-root

.EXAMPLE
    ./build/pack-msix.ps1
#>
[CmdletBinding()]
param(
    # Semantic version. Defaults to Directory.Build.props, matching build/pack.ps1.
    [string]$Version,

    [string]$Configuration = 'Release',

    # Optional overrides. By default, both values come from Directory.Build.props.
    [string]$Publisher,

    [string]$IdentityName,

    # Reuse an existing publish folder instead of rebuilding it.
    [switch]$SkipPublish,

    # Target architecture. "all" builds both and wraps them in a single .msixbundle, which is what
    # Partner Center wants: one submission that serves Intel/AMD and Arm64 devices.
    [ValidateSet('x64', 'arm64', 'all')]
    [string]$Architecture = 'all'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProj = Join-Path $repoRoot 'src/Scribe.App/Scribe.App.csproj'
$overlayProj = Join-Path $repoRoot 'src/Scribe.Overlay/Scribe.Overlay.csproj'
$outputDir = Join-Path $repoRoot 'releases'

# MSIX ProcessorArchitecture values are 'x64' and 'arm64'; the overlay's WinUI Platform is 'x64'
# and 'ARM64'. They are spelled differently, so both are carried explicitly rather than derived.
# @(...) wraps the switch because a switch unrolls a single-element array back to a bare Hashtable,
# whose .Count reports its KEY count (2), not one target.
$targets = @(switch ($Architecture) {
    'x64'   { , @{ Runtime = 'win-x64';   Msix = 'x64';   OverlayPlatform = 'x64' } }
    'arm64' { , @{ Runtime = 'win-arm64'; Msix = 'arm64'; OverlayPlatform = 'ARM64' } }
    'all'   { @(
                @{ Runtime = 'win-x64';   Msix = 'x64';   OverlayPlatform = 'x64' },
                @{ Runtime = 'win-arm64'; Msix = 'arm64'; OverlayPlatform = 'ARM64' }
              ) }
})

$propsPath = Join-Path $repoRoot 'Directory.Build.props'
[xml]$props = Get-Content $propsPath
$sourceVersion = [string]$props.Project.PropertyGroup.VersionPrefix
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $sourceVersion }
if ($Version -ne $sourceVersion) {
    throw "Requested version $Version does not match Directory.Build.props version $sourceVersion."
}

# MSIX requires a four-part version and reserves the revision field for Store use, so it must be 0.
$msixVersion = "$Version.0"
$storeIdentityName = [string]$props.Project.PropertyGroup.StoreIdentityName
$storeIdentityPublisher = [string]$props.Project.PropertyGroup.StoreIdentityPublisher
$displayName = [string]$props.Project.PropertyGroup.StoreProductDisplayName
$publisherDisplay = [string]$props.Project.PropertyGroup.StorePublisherDisplayName
if ([string]::IsNullOrWhiteSpace($IdentityName)) { $IdentityName = $storeIdentityName }
if ([string]::IsNullOrWhiteSpace($Publisher)) { $Publisher = $storeIdentityPublisher }
if ([string]::IsNullOrWhiteSpace($IdentityName)) {
    throw 'Directory.Build.props must define StoreIdentityName exactly as it appears in Partner Center.'
}
if ([string]::IsNullOrWhiteSpace($Publisher)) {
    throw 'Directory.Build.props must define StoreIdentityPublisher exactly as it appears in Partner Center.'
}
if ([string]::IsNullOrWhiteSpace($displayName)) {
    throw 'Directory.Build.props must define StoreProductDisplayName as a reserved Partner Center app name.'
}
if ([string]::IsNullOrWhiteSpace($publisherDisplay)) {
    throw 'Directory.Build.props must define StorePublisherDisplayName exactly as it appears in Partner Center.'
}

Write-Host "==> Scribe MSIX  v$msixVersion  ($Configuration)  architectures: $(($targets.Msix) -join ', ')" -ForegroundColor Cyan
Write-Host "==> Identity: $IdentityName | Publisher: $Publisher | Publisher display: $publisherDisplay" -ForegroundColor Cyan

# Drop any previous bundle for this version before building anything. Otherwise a run where x64
# packs and arm64 then fails leaves the PREVIOUS run's bundle sitting beside fresh
# single-architecture packages, where it still looks like a valid submission artifact despite no
# longer matching what was just built.
$bundlePath = Join-Path $outputDir "Scribe-$Version.msixbundle"
if (Test-Path $bundlePath) { Remove-Item $bundlePath -Force }

. (Join-Path $repoRoot 'scripts/Model-Manifest.ps1')
. (Join-Path $repoRoot 'scripts/Payload-Architecture.ps1')

# --- 1. Locate the Windows SDK packaging tools -----------------------------------------------------
# Prefer the tools built for this machine's architecture, but accept the x64 ones: they run under
# emulation on an Arm64 build host, so a missing arm64 SDK folder must not fail the build.
function Get-SdkTool([string]$name) {
    $hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $candidates = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter $name -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\(x64|arm64)\\' } |
        Sort-Object @{ Expression = { $_.FullName -match "\\$hostArch\\" }; Descending = $true }, @{ Expression = 'FullName'; Descending = $true }
    if ($candidates.Count -eq 0) { return $null }
    return $candidates[0].FullName
}

$makeAppx = Get-SdkTool 'makeappx.exe'
if (-not $makeAppx) {
    throw 'makeappx.exe was not found. Install the Windows 10/11 SDK to build an MSIX package.'
}
Write-Host "==> Using $makeAppx" -ForegroundColor Green

# Shared logo generator: the brand mark is loaded per call so no GDI handle is held across the
# (potentially long) publish steps between architectures.
$brandPng = Join-Path $repoRoot 'docs/icon.png'
if (-not (Test-Path $brandPng)) { throw "Brand image missing: $brandPng" }
Add-Type -AssemblyName System.Drawing

function Write-Logo([string]$target, [int]$width, [int]$height) {
    $source = [System.Drawing.Image]::FromFile($brandPng)
    try {
        $bitmap = New-Object System.Drawing.Bitmap $width, $height
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.Clear([System.Drawing.Color]::Transparent)
                # Preserve aspect ratio and centre the mark inside non-square logo sizes.
                $scale = [Math]::Min($width / $source.Width, $height / $source.Height)
                $w = [int]($source.Width * $scale)
                $h = [int]($source.Height * $scale)
                $graphics.DrawImage($source, [int](($width - $w) / 2), [int](($height - $h) / 2), $w, $h)
            }
            finally { $graphics.Dispose() }
            $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $bitmap.Dispose() }
    }
    finally { $source.Dispose() }
}

# --- 2. Build one architecture into a staged MSIX --------------------------------------------------
# Publishes app + matching overlay, verifies the payload really is that architecture, generates the
# Store logos, writes the manifest, and packs a single-architecture .msix.
function New-ScribeMsix {
    param(
        [Parameter(Mandatory)][string]$Runtime,
        [Parameter(Mandatory)][string]$MsixArchitecture,
        [Parameter(Mandatory)][string]$OverlayPlatform
    )

    $publishDir = Join-Path $repoRoot "publish/msix-$Runtime"
    $stageDir = Join-Path $repoRoot "publish/msix-stage-$Runtime"

    if (-not $SkipPublish) {
        if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
        Write-Host "==> dotnet publish (self-contained, $Runtime)..." -ForegroundColor Cyan
        # Out-Host, not the pipeline: this function returns the package path, and anything an
        # external command writes to stdout would otherwise be returned alongside it and end up
        # being treated as a package to bundle.
        dotnet publish $appProj -c $Configuration -r $Runtime --self-contained true -p:Version=$Version -o $publishDir | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Runtime." }

        Write-Host "==> dotnet publish overlay (self-contained WinUI 3, $OverlayPlatform)..." -ForegroundColor Cyan
        dotnet publish $overlayProj -c $Configuration -r $Runtime --self-contained true -p:Platform=$OverlayPlatform -p:Version=$Version -o (Join-Path $publishDir 'Overlay') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish (overlay) failed for $Runtime." }
    }

    if (-not (Test-Path (Join-Path $publishDir 'Scribe.exe'))) { throw "Scribe.exe missing in $publishDir" }

    # The Store rejects a bundle whose declared architecture does not match its payload, and an
    # emulated package would pass certification while running slowly, so assert it here.
    Test-ScribePayloadArchitecture -PublishDir $publishDir -Runtime $Runtime
    Test-ScribeRuntimeModels -ModelsDir (Join-Path $publishDir 'models') -VerifyHashes
    Write-Host '==> Runtime model payload verified.' -ForegroundColor Green

    # --- 3. Stage the package layout ---------------------------------------------------------------
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $stageDir -Recurse -Force

    # Store logos are generated from the same brand mark the app ships, so the listing, Start menu and
    # taskbar can never drift from the in-app icon.
    $assetsDir = Join-Path $stageDir 'Assets'
    New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null

    Write-Logo (Join-Path $assetsDir 'Square44x44Logo.png') 44 44
    Write-Logo (Join-Path $assetsDir 'Square44x44Logo.targetsize-44_altform-unplated.png') 44 44
    Write-Logo (Join-Path $assetsDir 'Square150x150Logo.png') 150 150
    Write-Logo (Join-Path $assetsDir 'StoreLogo.png') 50 50
    Write-Logo (Join-Path $assetsDir 'Wide310x150Logo.png') 310 150
    Write-Host '==> Store logos generated from the brand mark.' -ForegroundColor Green

    # --- 4. Write the manifest ---------------------------------------------------------------------
    # runFullTrust is required for a desktop Win32 app. The microphone capability is declared because
    # dictation captures audio; nothing is transmitted, which the Store listing states explicitly.
    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap rescap">

  <Identity
    Name="$IdentityName"
    Publisher="$Publisher"
    Version="$msixVersion"
    ProcessorArchitecture="$MsixArchitecture" />

  <Properties>
    <DisplayName>$displayName</DisplayName>
    <PublisherDisplayName>$publisherDisplay</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
    <Description>Private, fully offline push-to-talk voice dictation for Windows.</Description>
  </Properties>

  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>

  <Resources>
    <Resource Language="en-us" />
  </Resources>

  <Applications>
    <Application Id="Scribe" Executable="Scribe.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="$displayName"
        Description="Private, fully offline push-to-talk voice dictation for Windows."
        BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" />
      </uap:VisualElements>
    </Application>
  </Applications>

  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
    <DeviceCapability Name="microphone" />
  </Capabilities>
</Package>
"@

    $manifestPath = Join-Path $stageDir 'AppxManifest.xml'
    Set-Content -Path $manifestPath -Value $manifest -Encoding utf8
    Write-Host "==> Manifest written: $manifestPath" -ForegroundColor Green

    # --- 5. Build the package ----------------------------------------------------------------------
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    $msixPath = Join-Path $outputDir "Scribe-$Version-$Runtime.msix"
    if (Test-Path $msixPath) { Remove-Item $msixPath -Force }

    Write-Host "==> makeappx pack ($MsixArchitecture)..." -ForegroundColor Cyan
    & $makeAppx pack /d $stageDir /p $msixPath /o | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed for $Runtime." }

    $size = [Math]::Round((Get-Item $msixPath).Length / 1MB, 1)
    Write-Host "==> MSIX written: $msixPath ($size MB)" -ForegroundColor Green
    return $msixPath
}

$packages = @(foreach ($target in $targets) {
    New-ScribeMsix -Runtime $target.Runtime -MsixArchitecture $target.Msix -OverlayPlatform $target.OverlayPlatform
})

# Fail loudly rather than handing makeappx something that is not a package. Stray stdout from a
# build tool leaking into this list once produced a confusing "cannot find path" mid-bundle.
foreach ($package in $packages) {
    if (-not (Test-Path -LiteralPath $package -PathType Leaf) -or [IO.Path]::GetExtension($package) -ne '.msix') {
        throw "Expected only .msix paths from the packaging step but got: $package"
    }
}
if ($packages.Count -ne $targets.Count) {
    throw "Expected $($targets.Count) package(s) but produced $($packages.Count)."
}

# --- 6. Bundle both architectures for a single Store submission ------------------------------------
# Partner Center takes one bundle that serves every device, rather than a submission per
# architecture. Windows installs only the matching architecture from it, so an Arm64 PC never
# downloads the x64 payload.
$submission = $packages
if ($packages.Count -gt 1) {
    $bundleInput = Join-Path $repoRoot 'publish/msix-bundle'
    if (Test-Path $bundleInput) { Remove-Item $bundleInput -Recurse -Force }
    New-Item -ItemType Directory -Path $bundleInput -Force | Out-Null
    foreach ($package in $packages) { Copy-Item $package $bundleInput }

    Write-Host '==> makeappx bundle...' -ForegroundColor Cyan
    & $makeAppx bundle /d $bundleInput /p $bundlePath /bv $msixVersion /o
    if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle failed.' }

    $bundleSize = [Math]::Round((Get-Item $bundlePath).Length / 1MB, 1)
    Write-Host "==> MSIX bundle written: $bundlePath ($bundleSize MB)" -ForegroundColor Green
    $submission = @($bundlePath)
}

Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Yellow
Write-Host '  1. Validate with the Windows App Certification Kit before submitting.'
Write-Host "  2. Upload $(Split-Path -Leaf $submission[0]) in Partner Center. Microsoft signs it; do not sign it yourself."
