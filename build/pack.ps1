#requires -Version 7.0
<#
.SYNOPSIS
    Builds a signed, GitHub-release-backed Velopack installer + delta updates for Scribe.

.DESCRIPTION
    Scribe self-updates at runtime from its GitHub Releases (see Infrastructure/UpdateService.cs).
    This script produces the matching release artifacts:

        1. dotnet publish  -> a self-contained win-x64 build (no .NET install required on the user PC).
        2. vpk pack        -> Setup.exe (installer), a full .nupkg, delta packages, and RELEASES.
        3. vpk upload       -> attaches those artifacts to a GitHub Release (optional, -Publish).

        Production packaging is signed by default. The local self-signed certificate is selected from
        Cert:\CurrentUser\My by the thumbprint in signing\Scribe-CodeSigning.cer. Two signing paths exist:

            * Azure Trusted Signing — pass -AzureTrustedSignFile
        pointing at a Trusted Signing metadata JSON. Requires the `vpk` Azure signing prerequisites.
        Docs: https://learn.microsoft.com/azure/trusted-signing/
            * Local certificate store — default, or pass -SigningCertificateThumbprint explicitly.

        Unsigned output requires the explicit -AllowUnsigned switch.

.LINK
    https://docs.velopack.io/                         (Velopack)
    https://docs.velopack.io/integrating/cli           (vpk command reference)
    https://learn.microsoft.com/azure/trusted-signing/ (Azure Trusted Signing)

.EXAMPLE
    # Signed local build using signing\Scribe-CodeSigning.cer + CurrentUser\My:
    ./build/pack.ps1

.EXAMPLE
    # Intentional unsigned local test build:
    ./build/pack.ps1 -AllowUnsigned
#>
[CmdletBinding()]
param(
    # Semantic version for this release. Keep in sync with Directory.Build.props (<VersionPrefix>).
    [string]$Version,

    # Build configuration.
    [string]$Configuration = 'Release',

    # owner/repo the installer pulls updates from. Must match RepositoryUrl in Directory.Build.props.
    [string]$GitHubRepo = 'ChrisMcKee1/scribe',

    # Path to an Azure Trusted Signing metadata JSON. When set, vpk signs the artifacts with it.
    [string]$AzureTrustedSignFile,

    # Code-signing certificate in Cert:\CurrentUser\My. Defaults to signing\Scribe-CodeSigning.cer.
    [string]$SigningCertificateThumbprint,

    # RFC 3161 timestamp endpoint used for local Authenticode signing.
    [string]$TimestampServerUrl = 'http://timestamp.digicert.com',

    # Explicit escape hatch for local unsigned test artifacts.
    [switch]$AllowUnsigned,

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
$publicCertificatePath = Join-Path $repoRoot 'signing/Scribe-CodeSigning.cer'
$privateRootCertificatePath = Join-Path $repoRoot 'signing/Scribe-Root-CA.cer'
$codeSigningOid = '1.3.6.1.5.5.7.3.3'

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $candidate) { throw 'signtool.exe was not found. Install the Windows SDK.' }
    return $candidate.FullName
}

function Resolve-LocalSigningCertificate {
    if ([string]::IsNullOrWhiteSpace($script:SigningCertificateThumbprint) -and
        (Test-Path $publicCertificatePath)) {
        $publicCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($publicCertificatePath)
        $script:SigningCertificateThumbprint = $publicCertificate.Thumbprint
    }

    if ([string]::IsNullOrWhiteSpace($script:SigningCertificateThumbprint)) { return $null }
    $normalized = ($script:SigningCertificateThumbprint -replace '\s', '').ToUpperInvariant()
    $matches = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $normalized })
    if ($matches.Count -ne 1) { throw "Expected exactly one CurrentUser signing certificate $normalized; found $($matches.Count)." }

    $certificate = $matches[0]
    if (-not $certificate.HasPrivateKey) { throw "Signing certificate $normalized has no private key." }
    if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) {
        throw "Signing certificate $normalized is not currently valid."
    }
    $ekuExtension = $certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        Select-Object -First 1
    $hasCodeSigningEku = $ekuExtension -and
        @($ekuExtension.EnhancedKeyUsages | Where-Object { $_.Value -eq $codeSigningOid }).Count -eq 1
    if (-not $hasCodeSigningEku) {
        throw "Signing certificate $normalized does not include the Code Signing EKU."
    }
    return $certificate
}

function Assert-CustomSigningChain {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Signer,
        [Parameter(Mandatory)]
        [string]$RootCertificatePath
    )

    $root = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($RootCertificatePath)
    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    $chain.ChainPolicy.TrustMode = [System.Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
    $chain.ChainPolicy.CustomTrustStore.Add($root)
    if (-not $chain.Build($Signer)) {
        $status = ($chain.ChainStatus | ForEach-Object { $_.StatusInformation.Trim() }) -join '; '
        throw "Custom Scribe signing chain validation failed: $status"
    }
    $validatedRoot = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate
    if ($validatedRoot.Thumbprint -ne $root.Thumbprint) {
        throw "Signing chain terminated at unexpected root $($validatedRoot.Thumbprint)."
    }
}

function Assert-AuthenticodeFile {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [string]$Thumbprint,
        [string]$RootCertificatePath
    )
    if (-not (Test-Path $Path -PathType Leaf)) { throw "Signed artifact missing: $Path" }

    $signature = Get-AuthenticodeSignature $Path
    if (-not $signature.SignerCertificate) {
        throw "Authenticode signature missing from ${Path}: $($signature.StatusMessage)"
    }
    if ($Thumbprint -and $signature.SignerCertificate.Thumbprint -ne $Thumbprint) {
        throw "Unexpected signer for ${Path}: $($signature.SignerCertificate.Thumbprint)"
    }
    if (-not $signature.TimeStamperCertificate) {
        throw "Authenticode timestamp missing from $Path."
    }

    if ($signature.Status -eq 'Valid') {
        $signTool = Find-SignTool
        & $signTool verify /pa /all /tw $Path | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "SignTool verification failed for $Path." }
        return
    }

    $isExpectedPrivateRootFailure = $false
    if ($RootCertificatePath -and $signature.Status -eq 'UnknownError') {
        $root = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($RootCertificatePath)
        $systemChain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
        $systemChain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $systemChain.ChainPolicy.ExtraStore.Add($root)
        $systemChainValid = $systemChain.Build($signature.SignerCertificate)
        $systemStatuses = @($systemChain.ChainStatus | ForEach-Object { $_.Status })
        $isExpectedPrivateRootFailure =
            -not $systemChainValid -and
            $systemStatuses.Count -gt 0 -and
            @($systemStatuses | Where-Object {
                $_ -ne [System.Security.Cryptography.X509Certificates.X509ChainStatusFlags]::UntrustedRoot
            }).Count -eq 0
    }
    if (-not $isExpectedPrivateRootFailure) {
        throw "Authenticode verification failed for ${Path}: $($signature.StatusMessage)"
    }

    Assert-CustomSigningChain `
        -Signer $signature.SignerCertificate `
        -RootCertificatePath $RootCertificatePath
    Write-Host "Verified Authenticode integrity, timestamp, signer, and pinned Scribe root for $Path."
}

function Assert-AuthenticodeArchiveEntry {
    param(
        [Parameter(Mandatory)] [string]$ArchivePath,
        [Parameter(Mandatory)] [string]$EntryPath,
        [Parameter(Mandatory)] [string]$DestinationPath,
        [string]$Thumbprint,
        [string]$RootCertificatePath
    )

    if (-not (Test-Path $ArchivePath -PathType Leaf)) { throw "Release archive missing: $ArchivePath" }
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $normalizedEntryPath = $EntryPath.Replace('\', '/')
        $entry = $archive.Entries |
            Where-Object { $_.FullName -eq $normalizedEntryPath } |
            Select-Object -First 1
        if (-not $entry) { throw "Archive entry missing from ${ArchivePath}: $EntryPath" }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $DestinationPath, $true)
    }
    finally {
        $archive.Dispose()
    }

    Assert-AuthenticodeFile `
        -Path $DestinationPath `
        -Thumbprint $Thumbprint `
        -RootCertificatePath $RootCertificatePath
}

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

if ($AzureTrustedSignFile -and $SigningCertificateThumbprint) {
    throw 'Choose either Azure Trusted Signing or a local signing certificate, not both.'
}

$localSigningCertificate = $null
if (-not $AzureTrustedSignFile) {
    $localSigningCertificate = Resolve-LocalSigningCertificate
}

if (-not $AzureTrustedSignFile -and -not $localSigningCertificate -and -not $AllowUnsigned) {
    throw 'No signing certificate is available. Run scripts/New-ScribeCodeSigningCertificate.ps1, or pass -AllowUnsigned for an intentional local test build.'
}

if ($localSigningCertificate) {
    Write-Host "==> Signing certificate: $($localSigningCertificate.Subject) [$($localSigningCertificate.Thumbprint)]" -ForegroundColor Green
}

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
# Build the vpk argument list, layering signing on only when requested.
$packArgs = @(
    'pack',
    '--packId', $packId,
    '--packVersion', $Version,
    '--packDir', $publishDir,
    '--mainExe', $mainExe,
    '--outputDir', $releaseDir,
    '--channel', $runtime
)
if ($AzureTrustedSignFile) {
    Write-Host "==> Signing with Azure Trusted Signing: $AzureTrustedSignFile" -ForegroundColor Cyan
    $packArgs += @('--azureTrustedSignFile', $AzureTrustedSignFile)
}
elseif ($localSigningCertificate) {
    $signParams = "/sha1 $($localSigningCertificate.Thumbprint) /s My /fd SHA256 /tr $TimestampServerUrl /td SHA256 /d `"Scribe`" /du `"https://github.com/ChrisMcKee1/scribe`""
    Write-Host '==> Signing with CurrentUser Authenticode certificate.' -ForegroundColor Cyan
    $packArgs += @('--signParams', $signParams)
}
else {
    Write-Warning 'Unsigned output explicitly requested with -AllowUnsigned.'
}

Write-Host '==> vpk pack...' -ForegroundColor Cyan
vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }

if ($localSigningCertificate -or $AzureTrustedSignFile) {
    $expectedThumbprint = if ($localSigningCertificate) { $localSigningCertificate.Thumbprint } else { $null }
    $verificationRootPath = if ($localSigningCertificate) { $privateRootCertificatePath } else { $null }
    $verificationDir = Join-Path ([System.IO.Path]::GetTempPath()) ("scribe-signature-verification-{0}" -f [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $verificationDir | Out-Null
    try {
        $portablePath = Join-Path $releaseDir 'Scribe-win-x64-Portable.zip'
        $fullPackagePath = Join-Path $releaseDir "Scribe-$Version-win-x64-full.nupkg"
        Assert-AuthenticodeArchiveEntry `
            -ArchivePath $portablePath `
            -EntryPath 'current/Scribe.exe' `
            -DestinationPath (Join-Path $verificationDir 'portable-Scribe.exe') `
            -Thumbprint $expectedThumbprint `
            -RootCertificatePath $verificationRootPath
        Assert-AuthenticodeArchiveEntry `
            -ArchivePath $portablePath `
            -EntryPath 'current/Overlay/Scribe.Overlay.exe' `
            -DestinationPath (Join-Path $verificationDir 'portable-Scribe.Overlay.exe') `
            -Thumbprint $expectedThumbprint `
            -RootCertificatePath $verificationRootPath
        Assert-AuthenticodeArchiveEntry `
            -ArchivePath $fullPackagePath `
            -EntryPath 'lib/app/Scribe.exe' `
            -DestinationPath (Join-Path $verificationDir 'package-Scribe.exe') `
            -Thumbprint $expectedThumbprint `
            -RootCertificatePath $verificationRootPath
        Assert-AuthenticodeArchiveEntry `
            -ArchivePath $fullPackagePath `
            -EntryPath 'lib/app/Overlay/Scribe.Overlay.exe' `
            -DestinationPath (Join-Path $verificationDir 'package-Scribe.Overlay.exe') `
            -Thumbprint $expectedThumbprint `
            -RootCertificatePath $verificationRootPath
        Assert-AuthenticodeFile `
            -Path (Join-Path $releaseDir 'Scribe-win-x64-Setup.exe') `
            -Thumbprint $expectedThumbprint `
            -RootCertificatePath $verificationRootPath
    }
    finally {
        Remove-Item $verificationDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host '==> Authenticode signatures and timestamps verified.' -ForegroundColor Green
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
