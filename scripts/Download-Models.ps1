#requires -Version 5.1
<#
.SYNOPSIS
    Downloads the Scribe speech models into src/Scribe.App/models (gitignored).

.DESCRIPTION
    Fetches the offline ASR model (NVIDIA Parakeet TDT 0.6b v3, int8) from HuggingFace, the
    English-only Parakeet Unified 0.6B (int8) and the Silero VAD v5 model from the sherpa-onnx
    GitHub release. Parakeet Unified comes as one archive, which is checked, unpacked and copied
    into its own folder, parakeet-unified-en-0.6b-int8.

    The script is idempotent and resumable:
      * Files already present with the expected size (and SHA-256, where known) are skipped.
      * Partial downloads are resumed via curl's -C - flag.
      * SHA-256 is verified for the large ONNX files and the archive; mismatches abort with guidance.

    Total download is about 1.2 GB. Models are written under -ModelsDir
    (default: ../src/Scribe.App/models, copied to the app's output at build time).

.PARAMETER ModelsDir
    Destination directory. Defaults to the app's 'src/Scribe.App/models' folder, where the build
    copies them next to the executable (and into the published installer).

.PARAMETER Force
    Re-download every file even if a valid copy already exists.

.EXAMPLE
    pwsh ./scripts/Download-Models.ps1
#>
[CmdletBinding()]
param(
    [string]$ModelsDir = (Join-Path $PSScriptRoot '..\src\Scribe.App\models'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$AsrRepo  = 'csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8'
$AsrBase  = "https://huggingface.co/$AsrRepo/resolve/main"
$VadBase  = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models'

. (Join-Path $PSScriptRoot 'Model-Manifest.ps1')

# Manifest: relative path under ModelsDir, source URL, expected size, optional SHA-256. A file that comes out of a
# release archive is fetched by the archive pass below instead.
$Manifest = @(
    $ScribeRuntimeModelManifest | Where-Object { -not $_.Archive } | ForEach-Object {
        $base = if ($_.Path -eq 'silero_vad_v5.onnx') { $VadBase } else { $AsrBase }
        [pscustomobject]@{ Path = $_.Path; Url = "$base/$($_.Path)"; Size = $_.Size; Sha256 = $_.Sha256 }
    }
    [pscustomobject]@{ Path = 'test_wavs/en.wav';  Url = "$AsrBase/test_wavs/en.wav";  Size = $null;     Sha256 = $null }
    [pscustomobject]@{ Path = 'test_wavs/de.wav';  Url = "$AsrBase/test_wavs/de.wav";  Size = $null;     Sha256 = $null }
    [pscustomobject]@{ Path = 'test_wavs/es.wav';  Url = "$AsrBase/test_wavs/es.wav";  Size = $null;     Sha256 = $null }
    [pscustomobject]@{ Path = 'test_wavs/fr.wav';  Url = "$AsrBase/test_wavs/fr.wav";  Size = $null;     Sha256 = $null }
)

$Curl = Join-Path $env:SystemRoot 'System32\curl.exe'
if (-not (Test-Path $Curl)) { $Curl = 'curl.exe' }   # fall back to PATH
# Windows' own bsdtar, which reads bzip2; Git's tar earlier on PATH would read C:\ as a remote host.
$Tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path $Tar)) { $Tar = 'tar.exe' }

function Test-Existing {
    param([string]$Dest, [Nullable[long]]$Size, [string]$Sha256)
    if (-not (Test-Path $Dest)) { return $false }
    $info = Get-Item $Dest
    if ($null -ne $Size -and $info.Length -ne $Size) { return $false }
    if ($Sha256) {
        $actual = (Get-FileHash $Dest -Algorithm SHA256).Hash
        if ($actual -ne $Sha256.ToUpperInvariant()) { return $false }
    }
    return $true
}

Write-Host "Scribe model downloader" -ForegroundColor Cyan
$resolved = [System.IO.Path]::GetFullPath($ModelsDir)
Write-Host "  Destination : $resolved"
Write-Host "  Files       : $($Manifest.Count) plus $($ScribeModelArchives.Count) archive(s)  (about 1.2 GB total)"
Write-Host ""

$downloaded = 0; $skipped = 0
foreach ($item in $Manifest) {
    $dest = Join-Path $resolved $item.Path
    $parent = Split-Path $dest -Parent
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

    if (-not $Force -and (Test-Existing -Dest $dest -Size $item.Size -Sha256 $item.Sha256)) {
        Write-Host ("  [skip] {0}" -f $item.Path) -ForegroundColor DarkGray
        $skipped++
        continue
    }

    Write-Host ("  [get ] {0}" -f $item.Path) -ForegroundColor Yellow
    if ($Force -and (Test-Path $dest)) { Remove-Item $dest -Force }
    $curlArgs = @('-L', '--fail', '--retry', '5', '--retry-delay', '2', '-C', '-', '-o', $dest, $item.Url)
    & $Curl @curlArgs
    if ($LASTEXITCODE -ne 0) { throw "Download failed for $($item.Path) (curl exit $LASTEXITCODE)." }

    if ($null -ne $item.Size) {
        $len = (Get-Item $dest).Length
        if ($len -ne $item.Size) { throw "Size mismatch for $($item.Path): got $len, expected $($item.Size). Re-run with -Force." }
    }
    if ($item.Sha256) {
        $actual = (Get-FileHash $dest -Algorithm SHA256).Hash
        if ($actual -ne $item.Sha256.ToUpperInvariant()) {
            throw "SHA-256 mismatch for $($item.Path).`n  expected $($item.Sha256)`n  actual   $actual`nRe-run with -Force."
        }
        Write-Host ("         sha256 ok") -ForegroundColor DarkGreen
    }
    $downloaded++
}

foreach ($name in @($ScribeRuntimeModelManifest | Where-Object { $_.Archive } | ForEach-Object { $_.Archive } | Select-Object -Unique)) {
    $archive = $ScribeModelArchives[$name]
    $files = @($ScribeRuntimeModelManifest | Where-Object { $_.Archive -eq $name })
    $missing = @($files | Where-Object { -not (Test-Existing -Dest (Join-Path $resolved $_.Path) -Size $_.Size -Sha256 $_.Sha256) })
    if (-not $Force -and $missing.Count -eq 0) {
        Write-Host ("  [skip] {0}" -f $archive.Destination) -ForegroundColor DarkGray
        $skipped += $files.Count
        continue
    }

    # Outside ModelsDir, so a build never copies a partial archive into the app; kept between runs so curl can resume.
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "scribe-models-$name"
    if ($Force -and (Test-Path $staging)) { Remove-Item $staging -Recurse -Force }
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    $archivePath = Join-Path $staging 'model.tar.bz2'

    Write-Host ("  [get ] {0} (archive)" -f $archive.Destination) -ForegroundColor Yellow
    if (-not (Test-Existing -Dest $archivePath -Size $archive.Size -Sha256 $archive.Sha256)) {
        & $Curl -L --fail --retry 5 --retry-delay 2 -C - -o $archivePath $archive.Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed for $($archive.Destination) (curl exit $LASTEXITCODE)." }
        $len = (Get-Item $archivePath).Length
        $actual = (Get-FileHash $archivePath -Algorithm SHA256).Hash
        if ($len -ne $archive.Size -or $actual -ne $archive.Sha256.ToUpperInvariant()) {
            Remove-Item $archivePath -Force
            throw "The $($archive.Destination) archive failed its size or SHA-256 check (got $len bytes, $actual). Run the script again."
        }
    }
    Write-Host ("         sha256 ok") -ForegroundColor DarkGreen

    $unpacked = Join-Path $staging 'unpacked'
    if (Test-Path $unpacked) { Remove-Item $unpacked -Recurse -Force }
    New-Item -ItemType Directory -Path $unpacked -Force | Out-Null
    & $Tar -xjf $archivePath -C $unpacked
    if ($LASTEXITCODE -ne 0) { throw "Could not unpack the $($archive.Destination) archive (tar exit $LASTEXITCODE)." }

    $source = Join-Path $unpacked $archive.Directory
    $destination = Join-Path $resolved $archive.Destination
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($item in $files) {
        $dest = Join-Path $resolved $item.Path
        Copy-Item (Join-Path $source (Split-Path $item.Path -Leaf)) $dest -Force
        if (-not (Test-Existing -Dest $dest -Size $item.Size -Sha256 $item.Sha256)) {
            throw "The unpacked $($item.Path) does not match the manifest. Re-run with -Force."
        }
    }
    foreach ($document in $archive.Documents) {
        $path = Join-Path $source $document
        if (Test-Path $path) { Copy-Item $path (Join-Path $destination $document) -Force }
    }

    Remove-Item $staging -Recurse -Force
    $downloaded += $files.Count
}

Write-Host ""
Write-Host ("Done. {0} downloaded, {1} already present." -f $downloaded, $skipped) -ForegroundColor Green
Write-Host "Models ready under: $resolved"
