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

    # Publisher subject that must match the identity reserved in Partner Center exactly.
    [string]$Publisher = 'CN=ChrisMcKee',

    # Package identity name reserved in Partner Center.
    [string]$IdentityName = 'ChrisMcKee.Scribe',

    # Reuse an existing publish folder instead of rebuilding it.
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProj = Join-Path $repoRoot 'src/Scribe.App/Scribe.App.csproj'
$overlayProj = Join-Path $repoRoot 'src/Scribe.Overlay/Scribe.Overlay.csproj'
$publishDir = Join-Path $repoRoot 'publish/msix-win-x64'
$stageDir = Join-Path $repoRoot 'publish/msix-stage'
$outputDir = Join-Path $repoRoot 'releases'
$runtime = 'win-x64'

$propsPath = Join-Path $repoRoot 'Directory.Build.props'
[xml]$props = Get-Content $propsPath
$sourceVersion = [string]$props.Project.PropertyGroup.VersionPrefix
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $sourceVersion }
if ($Version -ne $sourceVersion) {
    throw "Requested version $Version does not match Directory.Build.props version $sourceVersion."
}

# MSIX requires a four-part version and reserves the revision field for Store use, so it must be 0.
$msixVersion = "$Version.0"
$displayName = [string]$props.Project.PropertyGroup.Product
$publisherDisplay = [string]$props.Project.PropertyGroup.StorePublisherDisplayName
if ([string]::IsNullOrWhiteSpace($publisherDisplay)) {
    throw 'Directory.Build.props must define StorePublisherDisplayName exactly as it appears in Partner Center.'
}

Write-Host "==> Scribe MSIX  v$msixVersion  ($Configuration, $runtime)" -ForegroundColor Cyan
Write-Host "==> Identity: $IdentityName | Publisher: $Publisher | Publisher display: $publisherDisplay" -ForegroundColor Cyan

# --- 1. Locate the Windows SDK packaging tools -----------------------------------------------------
function Get-SdkTool([string]$name) {
    $candidates = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter $name -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending
    if ($candidates.Count -eq 0) { return $null }
    return $candidates[0].FullName
}

$makeAppx = Get-SdkTool 'makeappx.exe'
if (-not $makeAppx) {
    throw 'makeappx.exe was not found. Install the Windows 10/11 SDK to build an MSIX package.'
}
Write-Host "==> Using $makeAppx" -ForegroundColor Green

# --- 2. Publish the app and the overlay ------------------------------------------------------------
if (-not $SkipPublish) {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    Write-Host '==> dotnet publish (self-contained)...' -ForegroundColor Cyan
    dotnet publish $appProj -c $Configuration -r $runtime --self-contained true -p:Version=$Version -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    Write-Host '==> dotnet publish overlay (self-contained WinUI 3)...' -ForegroundColor Cyan
    dotnet publish $overlayProj -c $Configuration -r $runtime --self-contained true -p:Platform=x64 -p:Version=$Version -o (Join-Path $publishDir 'Overlay')
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (overlay) failed.' }
}

if (-not (Test-Path (Join-Path $publishDir 'Scribe.exe'))) { throw "Scribe.exe missing in $publishDir" }

. (Join-Path $repoRoot 'scripts/Model-Manifest.ps1')
Test-ScribeRuntimeModels -ModelsDir (Join-Path $publishDir 'models') -VerifyHashes
Write-Host '==> Runtime model payload verified.' -ForegroundColor Green

# --- 3. Stage the package layout -------------------------------------------------------------------
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
Copy-Item -Path (Join-Path $publishDir '*') -Destination $stageDir -Recurse -Force

# Store logos are generated from the same brand mark the app ships, so the listing, Start menu and
# taskbar can never drift from the in-app icon.
$assetsDir = Join-Path $stageDir 'Assets'
New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null
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

Write-Logo (Join-Path $assetsDir 'Square44x44Logo.png') 44 44
Write-Logo (Join-Path $assetsDir 'Square44x44Logo.targetsize-44_altform-unplated.png') 44 44
Write-Logo (Join-Path $assetsDir 'Square150x150Logo.png') 150 150
Write-Logo (Join-Path $assetsDir 'StoreLogo.png') 50 50
Write-Logo (Join-Path $assetsDir 'Wide310x150Logo.png') 310 150
Write-Host '==> Store logos generated from the brand mark.' -ForegroundColor Green

# --- 4. Write the manifest -------------------------------------------------------------------------
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
    ProcessorArchitecture="x64" />

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

# --- 5. Build the package --------------------------------------------------------------------------
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$msixPath = Join-Path $outputDir "Scribe-$Version-win-x64.msix"
if (Test-Path $msixPath) { Remove-Item $msixPath -Force }

Write-Host '==> makeappx pack...' -ForegroundColor Cyan
& $makeAppx pack /d $stageDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed.' }

$size = [Math]::Round((Get-Item $msixPath).Length / 1MB, 1)
Write-Host "==> MSIX written: $msixPath ($size MB)" -ForegroundColor Green
Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Yellow
Write-Host '  1. Reserve the app name in Partner Center and copy the assigned Identity/Publisher.'
Write-Host '  2. Re-run this script with -IdentityName and -Publisher matching that reservation exactly.'
Write-Host '  3. Validate with the Windows App Certification Kit before submitting.'
Write-Host '  4. Upload the .msix in Partner Center. Microsoft signs it; do not sign it yourself.'
