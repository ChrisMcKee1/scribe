#requires -Version 5.1
<#
.SYNOPSIS
    Downloads the Scribe speech models into src/Scribe.App/models (gitignored).

.DESCRIPTION
    Fetches the offline ASR model (NVIDIA Parakeet TDT 0.6b v3, int8) from HuggingFace
    and the Silero VAD v5 model from the sherpa-onnx GitHub release.

    The script is idempotent and resumable:
      * Files already present with the expected size (and SHA-256, where known) are skipped.
      * Partial downloads are resumed via curl's -C - flag.
      * SHA-256 is verified for the large ONNX files; mismatches abort with guidance.

    Total download is ~640 MB. Models are written under -ModelsDir
    (default: ../src/Scribe.App/models — copied to the app's output at build time).

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

# Manifest: relative path under ModelsDir, source URL, expected size, optional SHA-256.
$Manifest = @(
    [pscustomobject]@{ Path = 'encoder.int8.onnx'; Url = "$AsrBase/encoder.int8.onnx"; Size = 652184281; Sha256 = 'acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247' }
    [pscustomobject]@{ Path = 'decoder.int8.onnx'; Url = "$AsrBase/decoder.int8.onnx"; Size = 11845275;  Sha256 = '179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e' }
    [pscustomobject]@{ Path = 'joiner.int8.onnx';  Url = "$AsrBase/joiner.int8.onnx";  Size = 6355277;   Sha256 = '3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3' }
    [pscustomobject]@{ Path = 'tokens.txt';        Url = "$AsrBase/tokens.txt";        Size = 93939;     Sha256 = $null }
    [pscustomobject]@{ Path = 'test_wavs/en.wav';  Url = "$AsrBase/test_wavs/en.wav";  Size = $null;     Sha256 = $null }
    [pscustomobject]@{ Path = 'test_wavs/de.wav';  Url = "$AsrBase/test_wavs/de.wav";  Size = $null;     Sha256 = $null }
    [pscustomobject]@{ Path = 'test_wavs/es.wav';  Url = "$AsrBase/test_wavs/es.wav";  Size = $null;     Sha256 = $null }
    [pscustomobject]@{ Path = 'test_wavs/fr.wav';  Url = "$AsrBase/test_wavs/fr.wav";  Size = $null;     Sha256 = $null }
    [pscustomobject]@{ Path = 'silero_vad_v5.onnx'; Url = "$VadBase/silero_vad_v5.onnx"; Size = 2313101; Sha256 = $null }
)

$Curl = Join-Path $env:SystemRoot 'System32\curl.exe'
if (-not (Test-Path $Curl)) { $Curl = 'curl.exe' }   # fall back to PATH

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
Write-Host "  Files       : $($Manifest.Count)  (~640 MB total)"
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

Write-Host ""
Write-Host ("Done. {0} downloaded, {1} already present." -f $downloaded, $skipped) -ForegroundColor Green
Write-Host "Models ready under: $resolved"
